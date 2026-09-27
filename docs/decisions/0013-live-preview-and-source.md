# 0013-live-preview-and-source

- **Status:** Accepted
- **Date:** 2026-09-26

## Context

Nine phases in, the editor is a monospace `TextBox` showing raw Markdown. It is
honest - you can see exactly what is in the file, and what you see is what gets
saved - but it is the last big gap between this app and the tool people picture
when they picture it. Everything else now looks like Obsidian; the middle of the
window does not.

The obvious implementation is to replace the editor with a rendered view and keep
the raw text somewhere behind a toggle. That is a rewrite of the most fragile
component in the app, and the fragility is not hypothetical:

- **Reading `Editor.Text` back has crashed the process natively on this machine's
  WindowsAppSDK build**, more than once, in ways that survived several wrong
  theories ([architecture.md](../architecture.md#known-issues)). The `TextBox` in
  the middle of the window is the exact component involved.
- **Mutating controls from inside an input's own event handler crashes too.** The
  pointer-over animation crash (a `DoubleAnimation` aimed at a `Brush`) killed the
  app with no input at all, and re-entrant control mutation killed it on the first
  click. Both are documented, both were found by launching the app, and neither is
  the kind of thing a design document predicts.
- **No XAML popups of any kind**, so there is no editor-pane picker, no context
  menu, no diff popup.

A rewrite would put a new parser, a new rendering surface, a new focus model, a
new selection model and a new cursor-position problem all on top of the one
component that has already crashed the process four documented ways, with no way to
test any of it except by driving the app by hand.

## Decision

**A live preview *beside* the source, not a replacement for it.**

The window gets a preview pane. It renders the open note as Markdown, read-only,
and it is on or off with one button (`Ctrl+R`). The `TextBox` stays exactly where
it is, exactly as editable as it was, with the same save path and the same
behaviour when it crashes. The preview is a projection of the file, never the
source of it: saving writes the `TextBox`, never the preview.

The Markdown is rendered by a **small purpose-built renderer in Core**, not by a
third-party Markdown library. That is the part that deserves defending, because
"why not just use Markdig" is the obvious question:

- The renderer needs to be **testable without a UI**, which is the only way this
  component can be developed at all given the crash history. A library's pipeline
  is a black box behind an `HtmlWriter` or a Roslyn syntax tree; this app's editor
  is XAML, and XAML cannot be built in a unit test.
- It needs to produce a **small, closed set of shapes** the preview can draw -
  headings, paragraphs, lists, quotes, code, rules, links, emphasis - and nothing
  else. That is not a general Markdown implementation, and pretending to be one
  would be the thing to apologise for later.
- A dependency is a thing that has to be updated, audited and understood. The
  subset here is a few hundred lines of tests.

### What the first version renders, and what it does not

**Rendered:** headings 1-6, paragraphs, bullet and ordered lists (with their real
start number), block quotes including multi-paragraph ones, fenced code with its
language, horizontal rules, images (named, not fetched), inline code, bold and
italic, wiki links, and Markdown links.

**Not rendered, each with a test that says so:**

- **Tables.** They come out as the paragraphs they look like.
- **Task list checkboxes.** `- [ ]` renders as a bullet with the `[ ]` still in the
  text. Drawing a real checkbox means putting an interactive control in a read-only
  pane, which is the wrong thing to do and would be the first place the preview
  could be mistaken for an editor.
- **Nested lists**, flattened to one level. A half-nested list is harder to read
  than an honest flat one.
- **Setext headings** (`Title` then `=====`) and **HTML blocks**, which render as
  paragraphs.
- **Images are named, never fetched.** A preview that resolved a relative path or
  reached the network would be a preview with side effects.

The rule underneath all of it: **syntax the preview does not draw is never shown to
the reader as punctuation.** `**bold**` renders as "bold" with no stars. A missing
style is a limitation; a stray `**` is a bug, and one of the tests exists to keep
that line.

## Consequences

- **The editor cannot get worse.** The worst case for this phase is a preview that
  is subtly wrong, in a pane you can switch off. The `TextBox` and its save path
  are untouched, and the three crash-prone paths stay exactly as they were.
- Writing is still writing. Nobody is asked to learn a rich text editor whose
  round-trip is lossy, which is the failure mode of every "rendered" Markdown app:
  the file and the screen stop agreeing, and then the file stops being the truth.
- The renderer is Core, pure, and tested against a corpus of Markdown. It is also
  the piece most likely to need replacing - if inline spans and tables get
  rendered later, that is a renderer change, not a shell change, which is the
  whole reason for putting it there.
- A rendered pane is a **second surface for the same bytes**, so it is a second
  thing that can disagree with the file. The rule that keeps it honest: the preview
  never writes, and it says so in the code and in this document.
- Performance: rendering on every keystroke would mutate the visual tree from a
  text-changed handler, which is the documented crash. So the preview updates on a
  **debounce and off the dispatcher**, never synchronously while typing.
- Not in this phase: a rendered *edit* mode, syntax highlighting inside code blocks,
  and the gaps listed above. Each is named rather than pretended.
