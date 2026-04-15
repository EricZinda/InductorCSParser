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

This .txt file is ignored by the extension and is safe to delete, but it can
be useful context for new contributors or LLMs working in the repository.
