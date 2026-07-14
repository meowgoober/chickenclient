<div align="center">
  <img width="512" height="512" alt="ChickenClient Logo" src="https://github.com/user-attachments/assets/851caad5-b838-4f21-8dc2-16d17d554742" />
</div>


# ChickenClient

ChickenClient is a lightweight, interactive terminal-based irc client written in c# that brings modern IRC messaging features directly into your console.

---

## features

* **inline image attachments**
automatically detects image urls (.png, .jpg, .jpeg, .gif), highlights them in a bright cyan, and downloads them in the background. it then renders them directly in the terminal below the message using true 24-bit color ansi blocks.
* **smooth scroll buffer**
rendered images are saved directly in your channel's message history. they scroll up and move naturally with the chat whenever new messages come in, behaving just like attachments on modern chat platforms.
* **smart terminal input**
features a thread-safe terminal layout. incoming messages print cleanly above your cursor without breaking, splitting, or disrupting your active typing line.
* **multi-server & bouncer support**
easily connect directly to multiple irc networks or set up your znc/bnc bouncer profiles to stay connected.
* **desktop notifications**
sends native windows toast notifications and plays warning alerts if you receive a private message or if someone mentions your nickname while the console window is out of focus.

---

## basic commands

here are some of the essential commands to get you started:

* `/help` - show the full command helper list
* `/server add <name> <host> [port]` - configure a new irc server
* `/connect <name>` - connect to a configured server or bouncer
* `/join <#channel>` - join a chat room
* `/msg <target> <message>` - send a private message to a user
* `/switch <server> [channel]` - hop between your active server and channel windows
* `/quit` - exit the client safely
