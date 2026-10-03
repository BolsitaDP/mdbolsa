using MdBolsa.Contracts;
using MdBolsa.Core.Attachments;
using MdBolsa.Core.Links;
using MdBolsa.Core.Markdown;
using Windows.Storage;
using Windows.Storage.Streams;
using MdBolsa.Core.Search;
using MdBolsa.Core.Tags;
using MdBolsa.Core.Sync;
using MdBolsa.Core.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.Foundation;
using ShapePath = Microsoft.UI.Xaml.Shapes.Path;
using ShapeRectangle = Microsoft.UI.Xaml.Shapes.Rectangle;
using Windows.System;
using PathShape = Microsoft.UI.Xaml.Shapes.Path;

namespace MdBolsa_Desktop_WinUI;

// Sync: the automatic triggers (ticker, debounce, startup) and the manual one, plus
// sync settings and device tokens. Part of MainPage; see MainPage.xaml.cs.
public sealed partial class MainPage
{
    // --- Automatic sync (Phase 9 follow-up) --------------------------------

    // Sync used to be a button you pressed, which means a second device's edits
    // only arrived when you happened to think to ask. Now there are two triggers,
    // and neither of them is on the save path:
    //
    //   * a ticker that decides - locally, with no network - whether the periodic
    //     sync is due, and runs one if so;
    //   * a debounce rearmed on every write this app makes, so a burst of saves is
    //     one sync rather than ten.
    //
    // A note is already on disk before either of them looks at it, and both run off
    // the dispatcher, so typing never waits for the network. What they do wait for
    // is each other: _syncRunning keeps a tick from starting a second sync on top
    // of the first, which is the same re-entrancy that crashes this runtime.
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _syncTicker;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _syncDebounce;

    private static readonly TimeSpan SyncInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SyncDebounce = TimeSpan.FromSeconds(20);

    // How often the ticker *wakes up* to make a decision. Cheap: the decision is a
    // few comparisons and no network. Frequent enough that a sync happens within
    // half a minute of the interval elapsing, rare enough to be invisible.
    private static readonly TimeSpan SyncTick = TimeSpan.FromSeconds(30);

    private SyncTriggerState _syncState = SyncTriggerState.Never;
    private bool _syncRunning;

    // The write path calls this. Rearming a one-shot timer is the entire debounce:
    // twenty seconds of quiet, then one sync with everything in it.
    private void ScheduleSyncAfterWrite()
    {
        if (AppSession.ServerUrl is null) return;
        _syncDebounce.Stop();
        _syncDebounce.Start();
    }

    private void OnSyncTick(object? sender, object e)
    {
        if (_syncRunning || AppSession.VaultPath is null) return;

        var decision = SyncTrigger.Decide(_syncState, DateTimeOffset.UtcNow, SyncInterval);
        if (decision == SyncDecision.Paused)
        {
            // Said once, in the status line that is already there - not in a
            // dialog, and not again on every tick.
            if (_syncState.ConsecutiveFailures == 1)
            {
                StatusText.Text = "Automatic sync is paused after a failure. Press Sync to try again.";
            }

            return;
        }

        if (decision == SyncDecision.Run) _ = SyncNowAsync(automatic: true);
    }

    private void OnSyncDebounce(object? sender, object e)
    {
        if (_syncRunning || AppSession.VaultPath is null) return;
        _ = SyncNowAsync(automatic: true);
    }

    // Called once a vault is open, so an edit from another device is already here
    // when you start working instead of arriving five minutes later.
    private void SyncOnceOnStartup()
    {
        if (AppSession.ServerUrl is null || AppSession.VaultPath is null) return;
        _syncDebounce.Stop();
        _ = SyncNowAsync(automatic: true);
    }

    // --- Sync (Phase 9) ----------------------------------------------------

    private void OnSyncRibbonClicked(object sender, RoutedEventArgs e) => _ = SyncNowAsync(automatic: false);

    // Sync settings are edited inline: no dialogs on this runtime.
    private void OnSyncSettingsRibbonClicked(object sender, RoutedEventArgs e)
    {
        if (SyncSettingsPanel.Visibility == Visibility.Visible)
        {
            ShowTree();
            return;
        }

        SyncServerBox.Text = AppSession.ServerUrl ?? string.Empty;
        SyncTokenBox.Password = AppSession.ServerToken ?? string.Empty;
        DeviceNameBox.Text = DeviceName.ForThisMachine();
        RefreshTokenHelpText();
        ShowSidebarView(SyncSettingsPanel);
    }

    // The box is a PasswordBox, so its contents cannot be inspected to decide what
    // to say about them; the shape of the token is the honest proxy, and it is the
    // same rule the server uses to tell the two apart.
    private void RefreshTokenHelpText()
    {
        var token = SyncTokenBox.Password;
        if (token.Length == 0)
        {
            SyncTokenHelpText.Text =
                "No token yet. Paste the server's shared token, then press Create this device's token.";
            return;
        }

        SyncTokenHelpText.Text = token.StartsWith("mdb_", StringComparison.Ordinal)
            ? "This device has its own token. Revoke it from the server's device list if this machine is lost."
            : "Using the shared token. Press Create this device's token once to get one that can be revoked on its own.";
    }

    /// <summary>
    /// Trades the shared token in the box for one only this machine can use, and
    /// stores that instead.
    ///
    /// The shared token is what the owner typed, and it is a vault-wide credential:
    /// with it, anything can read and write every note and upload every attachment.
    /// This button is the way out of that, and it is a one-way door on purpose -
    /// the app does not keep a copy of the shared token, so after this the
    /// bootstrap secret exists only wherever the owner keeps it.
    /// </summary>
    private async void OnCreateDeviceTokenClicked(object sender, RoutedEventArgs e)
    {
        var url = SyncServerBox.Text.Trim();
        if (!Uri.TryCreate(url.Length == 0 ? AppSession.ServerUrl ?? "" : url, UriKind.Absolute, out var server))
        {
            StatusText.Text = "Set the server URL first.";
            return;
        }

        var sharedToken = SyncTokenBox.Password;
        if (sharedToken.Length == 0)
        {
            StatusText.Text = "Paste the server's shared token first - that is what gets exchanged.";
            return;
        }

        if (sharedToken.StartsWith("mdb_", StringComparison.Ordinal))
        {
            StatusText.Text = "That is already a device token. Paste the shared token to create a new one.";
            return;
        }

        var deviceName = DeviceNameBox.Text.Trim();
        if (deviceName.Length == 0) deviceName = DeviceName.ForThisMachine();

        CreateDeviceTokenButton.IsEnabled = false;
        CreateDeviceTokenButton.Content = "Asking the server...";

        try
        {
            using var http = new HttpClient { BaseAddress = server, Timeout = TimeSpan.FromSeconds(30) };
            var client = new DeviceTokenClient(http, sharedToken);
            var minted = await client.MintAsync(deviceName);

            // This is the moment the shared token stops being on this machine.
            AppSession.ServerUrl = server.ToString().TrimEnd('/');
            AppSession.ServerToken = minted.Token;
            SyncTokenBox.Password = minted.Token;
            RefreshTokenHelpText();

            StatusText.Text = $"This device is now \"{minted.Name}\". Save to keep it.";
        }
        catch (Exception ex)
        {
            // An unreachable server, a wrong token, a name the server refused: the
            // message from the client already says which, and inventing a vaguer
            // one here would throw that away.
            StatusText.Text = ex is SyncException ? ex.Message : $"Could not reach the server. {ex.Message}";
        }
        finally
        {
            CreateDeviceTokenButton.IsEnabled = true;
            CreateDeviceTokenButton.Content = "Create this device's token";
        }
    }

    private void OnSaveSyncSettingsClicked(object sender, RoutedEventArgs e)
    {
        var url = SyncServerBox.Text.Trim();
        if (url.Length > 0 &&
            (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
             parsed.Scheme is not ("http" or "https")))
        {
            StatusText.Text = "The sync server URL has to be an http(s) address, e.g. http://localhost:5080";
            return;
        }

        AppSession.ServerUrl = url.Length == 0 ? null : url;
        AppSession.ServerToken = SyncTokenBox.Password.Length == 0 ? null : SyncTokenBox.Password;

        // A manual sync is also the way back from a paused background sync, so
        // clear the failure count here: the person has just said "try again".
        _syncState = SyncTriggerState.Never;

        ShowTree();
        SyncOnceOnStartup();
        StatusText.Text = AppSession.ServerUrl is null
            ? "Sync is not configured. The sliders button in the ribbon sets a server."
            : $"Sync configured for {AppSession.ServerUrl} (device {AppSession.DeviceId.ToString()[..8]}).";
    }

    // `automatic` is not decoration: it decides whether a failure pauses the
    // background sync, and whether the status line says who asked for it. A sync
    // nobody pressed should not look like something the user did.
    private async Task SyncNowAsync(bool automatic)
    {
        if (_syncRunning) return;

        if (AppSession.VaultPath is null)
        {
            if (!automatic) StatusText.Text = "Open a vault first.";
            return;
        }

        if (!TryCreateSyncClient(out var client, out var error))
        {
            if (!automatic) StatusText.Text = error;
            return;
        }

        _syncRunning = true;
        _syncState = _syncState with { LastAttempt = DateTimeOffset.UtcNow };
        SyncRibbonButton.IsEnabled = false;
        StatusText.Text = "Syncing...";

        try
        {
            // The local indexes have to be current *before* the sync: the push side
            // works from the note list, and a note saved since the last scan would
            // otherwise not be offered to the server.
            RescanAndRefreshList();

            var result = await client.SyncAsync(AppSession.VaultPath);

            if (result.Failed)
            {
                _syncState = SyncTrigger.Failed(_syncState, DateTimeOffset.UtcNow);

                // An automatic failure pauses the background. A manual one says what
                // went wrong and stays paused until the next manual attempt, which
                // is the same rule: the difference is only that a person is watching.
                StatusText.Text = automatic
                    ? $"Background sync failed, so it is now paused: {result.Error}"
                    : $"Sync failed: {result.Error}";
                return;
            }

            _syncState = SyncTrigger.Succeeded(_syncState, DateTimeOffset.UtcNow);

            // Attachments go in the same pass and after the notes, so a note that
            // arrives is not a broken image reference for the few seconds its picture
            // takes to follow. They cannot conflict, so there is nothing to order
            // *between* them - only this, which is about what the reader sees.
            var attachments = await SyncAttachmentsAsync();

            // The pull may have written or deleted .md files, so the indexes and
            // every panel derived from them are stale until we scan again.
            RescanAndRefreshList();

            // Built in parts, because both things that can go wrong here - a conflict
            // and a failed attachment - are worth saying, and a branch that reports
            // one of them silently drops the other. That is how "attachments are not
            // syncing" hides behind an unrelated conflict message.
            var conflicts = AppSession.OpenSyncStateStore().GetConflicts();

            var message =
                $"Synced: {result.Pulled} pulled, {result.Pushed} pushed, {result.Deleted} deleted. " +
                $"Attachments: {attachments.Pulled} pulled, {attachments.Pushed} pushed" +
                (attachments.Failed ? $" - failed: {attachments.Error}" : ".");

            if (conflicts.Count > 0)
            {
                message += $" {conflicts.Count} note(s) changed in two places - nothing was " +
                           "overwritten. The warning button in the ribbon lists them.";
            }

            StatusText.Text = message;
        }
        finally
        {
            _syncRunning = false;
            SyncRibbonButton.IsEnabled = true;
        }
    }



    // A separate client from the note one, and its own HttpClient: attachments and
    // notes have different endpoints, different failure modes (a 32 MB body versus a
    // 4 KB one) and different retry behaviour, and sharing one would mean a failed
    // image upload reported the note sync as failed too.
    private async Task<AttachmentSyncResult> SyncAttachmentsAsync()
    {
        var url = AppSession.ServerUrl;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var baseAddress))
        {
            return AttachmentSyncResult.FailedResult("No sync server configured.");
        }

        if (!url.EndsWith('/')) baseAddress = new Uri(url + "/");

        using var http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromMinutes(5) };
        return await new AttachmentSyncClient(
            http,
            AppSession.DeviceId,
            AppSession.ServerToken ?? string.Empty,
            AppSession.OpenSyncStateStore(),
            AppSession.OpenAttachmentIndex())
            .SyncAsync(AppSession.VaultPath!);
    }

    private bool TryCreateSyncClient(out NoteSyncClient client, out string error)
    {
        client = null!;
        error = string.Empty;

        var url = AppSession.ServerUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            error = "Sync is not configured. The sliders button in the ribbon sets a server and a token.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(AppSession.ServerToken))
        {
            error = "No sync token configured. The sliders button in the ribbon sets one.";
            return false;
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseAddress) ||
            baseAddress.Scheme is not ("http" or "https"))
        {
            error = $"\"{url}\" is not a valid sync server address.";
            return false;
        }

        // A base address without a trailing slash makes every relative request drop
        // its last segment, which looks like a 404 for the wrong endpoint.
        if (!url.EndsWith('/')) baseAddress = new Uri(url + "/");

        var http = new HttpClient { BaseAddress = baseAddress, Timeout = TimeSpan.FromSeconds(30) };
        client = new NoteSyncClient(
            http,
            AppSession.DeviceId,
            AppSession.ServerToken,
            AppSession.OpenSyncStateStore(),
            AppSession.OpenVaultIndex());

        return true;
    }
}
