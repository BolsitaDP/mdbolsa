<#
.SYNOPSIS
  Runs the mdbolsa sync backend (PostgreSQL + the API) on this machine, without Docker.

.DESCRIPTION
  The backend is meant to live on the Raspberry Pi. Until then, development needs
  the same thing locally - and needs it to work when Docker Desktop doesn't. This
  script is the "no Docker" way: a portable PostgreSQL 16 (the same major version
  the compose files use) plus the API from source.

    start    install PostgreSQL on first use, start it, start the API, wait for /health
    stop     stop the API, then PostgreSQL (only processes this script started)
    status   what is running, and where
    reset    delete the local database (needs -Force); the next start makes a new one

  Everything lives under %LOCALAPPDATA%\MdBolsa.Backend.Dev (override with
  MDBOLSA_BACKEND_HOME) - deliberately NOT inside the repository: this repo is in a
  OneDrive folder, and a live database inside a synced folder gets its files locked
  and uploaded mid-write.

  Nothing is exposed: PostgreSQL listens on 127.0.0.1:5432 and the API on
  localhost:5080, with the same throwaway development credentials as
  docker-compose.yml and appsettings.Development.json. See docs/development.md.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\dev-backend.ps1 start
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('start', 'stop', 'status', 'reset')]
    [string]$Command = 'status',

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # Invoke-* progress bars make big downloads crawl in 5.1

# --- What we install, pinned ---------------------------------------------------
# EnterpriseDB's official Windows binaries - the build the PostgreSQL project links
# to. The hash is checked before anything is extracted or run, so a corrupt or
# swapped download is refused rather than executed.
$PgVersion = '16.10-1'
$PgUrl     = "https://get.enterprisedb.com/postgresql/postgresql-$PgVersion-windows-x64-binaries.zip"
$PgSha256  = 'ebb3b6af4fa69dea9951b66855bc4d42dc04e56ccb9aa7024ce3c58bd89d6b0c'

# --- Where things are ------------------------------------------------------------
$RepoRoot    = Split-Path -Parent $PSScriptRoot
$BackendHome = if ($env:MDBOLSA_BACKEND_HOME) { $env:MDBOLSA_BACKEND_HOME } else { Join-Path $env:LOCALAPPDATA 'MdBolsa.Backend.Dev' }
$Downloads   = Join-Path $BackendHome 'downloads'
$PgRoot      = Join-Path $BackendHome 'pg'
$DataDir     = Join-Path $BackendHome 'data'
$LogDir      = Join-Path $BackendHome 'logs'
$PidFile     = Join-Path $BackendHome 'server.pid'
$PgBin       = Join-Path $PgRoot 'bin'
$ServerProj  = Join-Path $RepoRoot 'src\Server\MdBolsa.Server\MdBolsa.Server.csproj'
$ServerDir   = Split-Path -Parent $ServerProj

# The same values as docker-compose.yml and appsettings.Development.json, on purpose:
# the e2e tests and the server's development default both expect exactly these.
$PgPort     = 5432
$PgDatabase = 'mdbolsa_dev'
$PgUser     = 'mdbolsa'
$PgPassword = 'mdbolsa_dev'
$ServerUrl  = 'http://localhost:5080'

function Say([string]$message)  { Write-Host "[backend] $message" }
function Fail([string]$message) { Write-Host "[backend] ERROR: $message" -ForegroundColor Red; exit 1 }

function Test-Listening([int]$port) {
    @(Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue).Count -gt 0
}

function Pg([string]$tool) { Join-Path $PgBin "$tool.exe" }

function Test-PostgresRunning {
    if (-not (Test-Path (Pg 'pg_ctl')) -or -not (Test-Path (Join-Path $DataDir 'PG_VERSION'))) { return $false }
    & (Pg 'pg_ctl') status -D $DataDir *> $null
    return ($LASTEXITCODE -eq 0)
}

# --- PostgreSQL: install, initialise, start ------------------------------------------
function Install-Postgres {
    if (Test-Path (Pg 'pg_ctl')) { return }

    if ($PgSha256 -eq '__PIN_ME__') { Fail 'No pinned SHA-256 for the PostgreSQL download. Refusing to run something unverified.' }
    if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) { Fail 'curl.exe was not found (it ships with Windows 10 and 11).' }

    New-Item -ItemType Directory -Force -Path $Downloads | Out-Null
    $zip = Join-Path $Downloads "postgresql-$PgVersion-windows-x64-binaries.zip"

    if (-not (Test-Path $zip)) {
        Say "Downloading PostgreSQL $PgVersion (about 300 MB, once)..."
        & curl.exe -L --fail --silent --show-error -o "$zip.part" $PgUrl
        if ($LASTEXITCODE -ne 0) { Remove-Item "$zip.part" -Force -ErrorAction SilentlyContinue; Fail 'The download failed.' }
        Move-Item "$zip.part" $zip
    }

    Say 'Verifying the download...'
    $actual = (Get-FileHash -Algorithm SHA256 $zip).Hash.ToLowerInvariant()
    if ($actual -ne $PgSha256.ToLowerInvariant()) {
        Remove-Item $zip -Force
        Fail "SHA-256 mismatch (expected $PgSha256, got $actual). The file was deleted; run again to re-download."
    }

    # Everything except pgAdmin, StackBuilder, docs and debug symbols: a server needs
    # bin, lib and share, and the rest is most of the archive.
    $tmp = Join-Path $BackendHome 'pg.extracting'
    if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null

    # Windows' own bsdtar by full path: from Git Bash, a bare `tar` is GNU tar, which
    # reads "C:\..." as a host name ("Cannot connect to C: resolve failed").
    $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
    if (-not (Test-Path $tar)) { Fail "tar.exe was not found at $tar (it ships with Windows 10 and 11)." }

    Say 'Extracting...'
    & $tar -xf $zip -C $tmp --exclude 'pgsql/pgAdmin 4' --exclude 'pgsql/StackBuilder' --exclude 'pgsql/doc' --exclude 'pgsql/symbols' --exclude 'pgsql/include'
    if ($LASTEXITCODE -ne 0) { Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue; Fail 'Extraction failed.' }
    if (-not (Test-Path (Join-Path $tmp 'pgsql\bin\postgres.exe'))) { Fail 'The archive did not contain pgsql\bin\postgres.exe.' }

    Move-Item (Join-Path $tmp 'pgsql') $PgRoot
    Remove-Item $tmp -Recurse -Force

    # The archive is a few times the size of what was kept from it, and it can be
    # fetched (and re-verified against the pinned hash) again if ever needed.
    Remove-Item $zip -Force
    Say "Installed to $PgRoot"
}

function Initialize-Cluster {
    if (Test-Path (Join-Path $DataDir 'PG_VERSION')) { return }

    Say 'Creating the database cluster (first run)...'
    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

    # initdb reads the superuser password from a file so it never appears on a command
    # line. The file is removed as soon as initdb returns.
    $pwFile = Join-Path $BackendHome 'pw.tmp'
    [IO.File]::WriteAllText($pwFile, $PgPassword)
    try {
        & (Pg 'initdb') -D $DataDir -U $PgUser -A scram-sha-256 --pwfile=$pwFile -E UTF8 --locale=C
        if ($LASTEXITCODE -ne 0) { Fail 'initdb failed.' }
    }
    finally {
        Remove-Item $pwFile -Force -ErrorAction SilentlyContinue
    }

    # Loopback only. The password is the throwaway one committed in docker-compose.yml,
    # so this must never be reachable from the network.
    Add-Content -Path (Join-Path $DataDir 'postgresql.conf') -Value @(
        '',
        '# mdbolsa dev backend',
        "listen_addresses = '127.0.0.1'",
        "port = $PgPort",
        "password_encryption = 'scram-sha-256'"
    )
}

function Start-Postgres {
    if (Test-PostgresRunning) { Say "PostgreSQL is already running on 127.0.0.1:$PgPort."; return }
    if (Test-Listening $PgPort) {
        Fail "Port $PgPort is taken by something that is not this script's PostgreSQL (the Docker stack? 'docker compose down' first)."
    }

    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    Say 'Starting PostgreSQL...'

    # pg_ctl leaves postgres.exe running, and on Windows a child inherits the parent's
    # inheritable handles - including this script's stdout. If that is a pipe (the
    # script run under a task runner, CI, `| something`), whoever reads the pipe waits
    # for end-of-file until PostgreSQL exits, so the script "never returns".
    # Redirecting to files does not fix it (PowerShell's redirection still passes the
    # original handles on, and WaitForExit then waits on its own pipes). ShellExecute
    # launches with a clean handle table, so that is what is used; pg_ctl's own output
    # goes to a hidden console and PostgreSQL's to the log file.
    $pgLog = Join-Path $LogDir 'postgres.log'
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Pg 'pg_ctl'
    $psi.Arguments = "start -D `"$DataDir`" -l `"$pgLog`" -w -t 60"
    $psi.UseShellExecute = $true
    $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $ctl = [System.Diagnostics.Process]::Start($psi)
    $ctl.WaitForExit()
    if ($ctl.ExitCode -ne 0) { Fail "PostgreSQL did not start (pg_ctl exit $($ctl.ExitCode)). See $pgLog" }

    $env:PGPASSWORD = $PgPassword
    try {
        $exists = & (Pg 'psql') -h 127.0.0.1 -p $PgPort -U $PgUser -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = '$PgDatabase'"
        if ($LASTEXITCODE -ne 0) { Fail 'Could not connect to PostgreSQL to check for the database.' }
        if (-not "$exists".Trim()) {
            Say "Creating database $PgDatabase..."
            & (Pg 'createdb') -h 127.0.0.1 -p $PgPort -U $PgUser $PgDatabase
            if ($LASTEXITCODE -ne 0) { Fail "Could not create database $PgDatabase." }
        }
    }
    finally {
        Remove-Item Env:\PGPASSWORD -ErrorAction SilentlyContinue
    }
}

# --- The API -------------------------------------------------------------------------
function Get-RecordedServer {
    # The process this script started: same id AND same start time (ids get reused),
    # and a command line that really is the server. Anything else is not ours.
    if (-not (Test-Path $PidFile)) { return $null }
    $parts = (Get-Content $PidFile -Raw).Trim().Split('|')
    if ($parts.Count -ne 2) { return $null }

    $proc = Get-Process -Id ([int]$parts[0]) -ErrorAction SilentlyContinue
    if (-not $proc) { return $null }
    if ($proc.StartTime.ToFileTimeUtc().ToString() -ne $parts[1]) { return $null }

    $cim = Get-CimInstance Win32_Process -Filter "ProcessId = $($proc.Id)" -ErrorAction SilentlyContinue
    if (-not $cim -or $cim.CommandLine -notmatch 'MdBolsa\.Server\.dll') { return $null }
    return $proc
}

function Start-Server {
    if (Get-RecordedServer) { Say "The API is already running on $ServerUrl."; return }
    if (Test-Listening 5080) { Fail 'Port 5080 is taken by something that is not this script''s API.' }
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { Fail 'dotnet was not found on PATH.' }

    Say 'Building the API...'
    & dotnet build $ServerProj -c Debug --nologo -v q
    if ($LASTEXITCODE -ne 0) { Fail 'The API did not build.' }

    $dll = Get-ChildItem (Join-Path $ServerDir 'bin\Debug') -Filter 'MdBolsa.Server.dll' -Recurse | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $dll) { Fail 'Could not find the built MdBolsa.Server.dll.' }

    New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
    $out = Join-Path $LogDir 'server.log'
    $err = Join-Path $LogDir 'server.err.log'

    # Development environment, so it picks up appsettings.Development.json (the
    # local connection string and the throwaway token). Set for this process only
    # and handed to the child.
    # Launched through ShellExecute (cmd /c, hidden, output redirected inside cmd) for
    # the same reason as pg_ctl above: a clean handle table, so a pipe around this
    # script is not held open for as long as the API lives. ShellExecute cannot set
    # environment variables on the child, so they are set here and inherited.
    $dotnet = (Get-Command dotnet).Source
    $previous = @{ env = $env:ASPNETCORE_ENVIRONMENT; urls = $env:ASPNETCORE_URLS }
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = $ServerUrl
    try {
        Say "Starting the API on $ServerUrl..."
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = Join-Path $env:SystemRoot 'System32\cmd.exe'
        $psi.Arguments = "/c `"`"$dotnet`" `"$($dll.FullName)`" >`"$out`" 2>`"$err`"`""
        $psi.WorkingDirectory = $ServerDir
        $psi.UseShellExecute = $true
        $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
        $wrapper = [System.Diagnostics.Process]::Start($psi)
    }
    finally {
        $env:ASPNETCORE_ENVIRONMENT = $previous.env
        $env:ASPNETCORE_URLS = $previous.urls
    }

    # The process to track is the server itself, not the cmd.exe around it.
    $proc = $null
    for ($i = 0; $i -lt 40 -and -not $proc; $i++) {
        $child = Get-CimInstance Win32_Process -Filter "ParentProcessId = $($wrapper.Id) AND Name = 'dotnet.exe'" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($child) { $proc = Get-Process -Id $child.ProcessId -ErrorAction SilentlyContinue } else { Start-Sleep -Milliseconds 250 }
    }
    if (-not $proc) { Fail "The API process did not start. See $err" }

    Set-Content -Path $PidFile -Value "$($proc.Id)|$($proc.StartTime.ToFileTimeUtc())" -NoNewline

    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 750
        if ($proc.HasExited) { break }
        try {
            $health = Invoke-RestMethod "$ServerUrl/health" -TimeoutSec 3
            Say "Up: environment=$($health.environment) database=$($health.database)"
            Say "     auth=$($health.authentication)"
            return
        }
        catch { }
    }

    Say 'The API did not become healthy. Last log lines:'
    foreach ($log in $out, $err) { if (Test-Path $log) { Get-Content $log -Tail 15 } }
    Fail "See $LogDir"
}

function Stop-Server {
    $proc = Get-RecordedServer
    if ($proc) {
        Say "Stopping the API (pid $($proc.Id))..."
        Stop-Process -Id $proc.Id
        $proc.WaitForExit(10000) | Out-Null
    }
    elseif (Test-Listening 5080) {
        Say 'Something is listening on 5080, but this script did not start it - leaving it alone.'
    }
    Remove-Item $PidFile -Force -ErrorAction SilentlyContinue
}

function Stop-Postgres {
    if (Test-PostgresRunning) {
        Say 'Stopping PostgreSQL...'
        & (Pg 'pg_ctl') stop -D $DataDir -m fast -w -t 60
        if ($LASTEXITCODE -ne 0) { Fail 'PostgreSQL did not stop cleanly.' }
    }
    elseif (Test-Listening $PgPort) {
        Say "Something is listening on $PgPort, but it is not this script's PostgreSQL - leaving it alone."
    }
}

# --- Commands ---------------------------------------------------------------------------
switch ($Command) {
    'start' {
        Install-Postgres
        Initialize-Cluster
        Start-Postgres
        Start-Server
        Say 'Ready. In the app: Sync settings -> server URL http://localhost:5080, token dev-token-not-a-secret.'
    }
    'stop' {
        Stop-Server
        Stop-Postgres
        Say 'Stopped.'
    }
    'status' {
        $pg = Test-PostgresRunning
        $api = Get-RecordedServer
        Say ("PostgreSQL : " + $(if ($pg) { "running on 127.0.0.1:$PgPort" } elseif (Test-Path (Join-Path $DataDir 'PG_VERSION')) { 'stopped' } else { 'not set up yet (run: start)' }))
        Say ("API        : " + $(if ($api) { "running, pid $($api.Id), $ServerUrl" } else { 'stopped' }))
        if ($api) {
            try {
                $h = Invoke-RestMethod "$ServerUrl/health" -TimeoutSec 3
                Say "health     : $($h.status), environment=$($h.environment), database=$($h.database), auth=$($h.authentication)"
            }
            catch { Say 'health     : the process is up but /health did not answer.' }
        }
        Say "Files      : $BackendHome"
        if (-not $pg -and -not $api) { exit 1 }
    }
    'reset' {
        if (-not $Force) { Fail 'reset deletes the local development database. Run it again with -Force if that is what you want.' }

        Stop-Server
        Stop-Postgres

        $resolved = [IO.Path]::GetFullPath($DataDir)
        $homeFull = [IO.Path]::GetFullPath($BackendHome)
        if (-not (Test-Path (Join-Path $DataDir 'PG_VERSION'))) { Say 'There is no local database to delete.'; return }
        if (-not $resolved.StartsWith($homeFull, [StringComparison]::OrdinalIgnoreCase)) { Fail "Refusing to delete ${resolved}: it is outside ${homeFull}." }

        Remove-Item $DataDir -Recurse -Force
        Say 'Local database deleted. The next start creates a fresh one.'
    }
}
