SVS Performance Monitor
A SimVizion Studios product

Shows what limits Farming Simulator 25 on your PC - your processor or your graphics card - with FPS and stutter.
It watches the game from outside. Nothing is added to the game, and it works with or without mods.

START
  Keep all files of this folder together, anywhere you like (for example Documents\SVS Performance Monitor).
  Double-click SVSPerfMonitor.exe. An SV icon appears next to the clock (perhaps under the ^ arrow), and the
  dashboard opens in your browser. Then start the game.

  Grey icon = waiting for the game. Coloured icon = watching it. Hover over the icon for what limits the game.
  Double-click the icon for the dashboard. Right-click it for the game log, Settings and Exit.

FIRST START
  A welcome page offers to set up frame times (FPS and stutter). Windows asks for permission once; then sign out
  of Windows and back in. Without it you still see processor and graphics loads.

SETTINGS (right-click the icon, Settings)
  Start with Windows, frame times, opening the dashboard on your phone (home network only), and more.
  Everything that needs Windows' permission says so before it asks.

SHARE WITH A GROUP
  Friends can watch each other's dashboard and game log through a group that one of you hosts.
  To share: Settings, "Share with a group": the group's address and your invite code (both from the host).
  Your Windows user name, computer name and personal folder names are removed before anything is sent.
  Stop any time: right-click the icon, "Stop sharing with my group".
  To watch: open the group's address in any browser and sign in with your invite code.

HOST A GROUP (for the one who runs it; needs a web server with HTTPS, such as Caddy)
  1. Settings, "Host a group on this PC": the group listens on this PC only, at 127.0.0.1:8766.
  2. Your web server forwards a name to it. Caddy (Caddyfile):
       perf.example.com:9443 {
           reverse_proxy 127.0.0.1:8766
       }
     plus a DNS record for perf.example.com pointing to this PC (and its router port forwards).
  3. Settings, "Add someone": each person gets their own invite code, shown once. Remove them there any time.
  Your own PC can share with its group too: use http://127.0.0.1:8766 as the address.

"WINDOWS PROTECTED YOUR PC"
  This program is not signed yet. If Windows shows that message, click "More info", then "Run anyway".

STOP
  Right-click the icon, Exit.

REMOVE
  Settings: turn off "Start with Windows" and "Run with full permissions" (if you turned them on), then Exit and
  delete this folder. Your settings are in %APPDATA%\SVSPerfMonitor (delete it too if you like).

FILES
  SVSPerfMonitor.exe   the program you start
  SVSPerfMonitor.com   the program itself (the .exe starts it; also the command line: SVSPerfMonitor --help)
  PresentMon-*.exe     Intel PresentMon, reads frame times (MIT licence, see PresentMon-LICENSE.txt)
  LICENSE.txt          this program's licence (MIT)
  .NET-LICENSE.txt, .NET-THIRD-PARTY-NOTICES.txt   the .NET runtime built into SVSPerfMonitor.com (MIT + notices)

Source and help: https://github.com/206airmail/FSPerfMonitor
