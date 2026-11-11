# KAONOMI.Chat

The Kaonomi chat in Avalonia, aligned on [`KAONOMI.ChatGodot`](../KAONOMI.ChatGodot/README.md): the same commands,
the same tool windows and the same behaviour, with the **full chat on a vertical screen** when one is connected.

## Run

```bash
dotnet run --project KAONOMI.Chat
```

It needs what the Godot chat needs (the Kaonomi server, its agent hosts, the LLM gateway or the model server); Godot
itself is not needed. Command line: `--prompt="…"` sends a first prompt, `--command=/alarm;/iq` runs commands,
`--theme=Matrix` starts with a theme, `--voice` starts in the voice mode, `--no-vertical` leaves the full chat of the
vertical screen closed.

## One code for both chats

The models, the view models and the services are **those of the Godot chat**, linked as they are from
`KAONOMI.ChatGodot/Shared` (namespaces `KAONOMI.Chat.*`): a change there is a change here. So are `config.yml`,
`config.json`, the sounds, the pictures of the 3D faces and the shaders. What is written here is the Avalonia side only:

| Folder | What |
|---|---|
| `MainWindow.cs` | The window laid out as the Godot chat: the bar of commands on the left, the desktop of the tool windows in the middle, the chat on the right |
| `Chat/` | The chat (`ChatPanel.cs`), the voice mode, the Markdown of the answers, and the full chat of the vertical screen (`VerticalChatWindow.cs`) |
| `Desk/` | The desktop of the windows (`DeskArea.cs`), the shell the views open their windows with (`Shell.cs`), the bar of commands (`Neocom.cs`), the dialogs, and the view generated for a view model that has no view of its own (`AutoView.cs`) |
| `Tools/` | One view per tool window, by family as in the Godot chat: `Data`, `Knowledge`, `Media`, `Work`, `Writing`, `Daily`, `Alarms` |
| `Avatar/` | The 3D animations of the presets, drawn with OpenGL from the shaders of the Godot chat |
| `Themes/`, `Styles/` | The themes and their animated backgrounds |

The behaviour of the chat comes with the shared view models: the server keeps the context of the conversation, a
thumb down asks the answer again to the escalation model, a command written in an answer (`/system`, `/ontology`…)
is run, `/cache` opens the cache builder, and so on.

## The full chat on a vertical screen

When a screen taller than wide is connected, the chat also opens **in full screen on that screen**
(`Chat/VerticalChatWindow.cs`): the 3D animation of the preset over the upper part (60 % of the height, as long as
the conversation keeps 520 pixels), the conversation and the input under it. It is a second view of the same chat:
the same conversation, model, preset and voice as the chat of the main window. Another screen than the one of the
main window is taken when there is one, the largest first.

- `Escape` closes it; `/fullchat` opens it again (without a vertical screen, `/fullchat` opens the voice mode, as in
  the Godot chat).
- `--no-vertical` on the command line leaves it closed at the start.
- The tools still open in the desktop of the main window.

## `_old/`

The former KAONOMI.Chat (October: its own models, services, view models and XAML views, a chat docked to the right
edge of the screen) is kept in `_old/`, not built. Nothing of it is used; it can be deleted once nothing is missed.
