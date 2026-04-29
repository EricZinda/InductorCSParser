Backlog Directory
=================

This directory is managed by the Backlog Viewer VS Code extension.

How it works
------------

The .backlog file next to this directory is a "pointer file" that tells the
extension where to find backlog items. Open that file in VS Code to see the
visual backlog editor.

Each item in the backlog is stored as a separate .md file in this directory.
Files are named with a numeric or alphanumeric prefix that controls sort order
(e.g. 0001-my-task.md, 0002-another-task.md). The prefix is purely for
ordering; the item's title comes from the content inside the file.

When you open the pointer file the extension normalizes filenames: it
renumbers them to remove gaps, fixes slugs that don't match the title, and
splits any file that contains multiple items. This is expected and harmless.

You can edit these .md files directly, or open the pointer file in VS Code
to use the visual editor. If you add new files by hand, they can have any
name you want as long as they sort in the order you want them. The extension
will normalize the filenames when you next open the pointer file.

What NOT to put inside an item
------------------------------

The extension supports several item formats. Which one this directory uses
is recorded on the "formatId:" line of the .backlog pointer file next to
this directory. Look there first to see which format is active.

Each format has a different rule for what separates one item from the next.
If a body contains the separator for the active format, the file gets split
into multiple items the next time the pointer file is opened. So when you
add an item by hand, follow the rule for the format in use:

For formatId "h1-heading" (H1 Headings):
  A line that starts with "# " begins a new item. Don't put one in the body. (Lines starting with "##" or deeper are fine.)

For formatId "h2-heading" (H2 Headings):
  A line that starts with "## " begins a new item. Don't put one in the body. (Lines starting with "#" alone or "###" or deeper are fine.)

For formatId "bullet" (Bullet Points):
  A line that starts with "-", "*", or "+" at the left margin (no leading whitespace) begins a new item. If you want a list inside an item, indent it by at least two spaces.

For formatId "paragraph" (Paragraphs):
  A blank line begins a new item. Don't put blank lines inside the body. Use a single newline if you need a line break.

For formatId "indentation" (Indentation):
  Any non-indented, non-blank line begins a new item. Every body line must start with whitespace.

This .txt file is ignored by the extension and is safe to delete, but it can
be useful context for new contributors or LLMs working in the repository.
