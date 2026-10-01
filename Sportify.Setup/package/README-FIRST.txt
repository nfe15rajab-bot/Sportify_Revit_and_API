SPORTIFY {{VERSION}} - sports and gardens on a roof - for Autodesk Revit 2025
================================================================================
Digital Tools and Methods - Group of Sports and Gardens, TH OWL (MID project),
in partnership with GOLDBECK. Open source (MIT License). Beta: results are
preliminary and do not replace the checks of an architect or engineer.

IN THIS ZIP
  Sportify-Setup-{{VERSION}}-Revit2025.exe   THE INSTALLER - the only file you need. It holds the
                                              Revit add-in, the web app, its local API and database,
                                              and a library (Revit templates, families, user guide)
  Sportify-Setup-{{VERSION}}-Revit2025.exe.sha256   checksum of the installer (optional)
  LICENSE_AGREEMENT.txt                       what setup asks you to accept
  THIRD_PARTY_NOTICES.txt                     the libraries and data by others that Sportify uses
  Sportify-Guide-for-the-Professor.pdf        the full user guide (from opening the app to the finished
                                              model, tips, the problems we met) with the BIM chart
  README-FIRST.txt                            this file
  Remove-Developer-Sportify.ps1               only for the team's own development computers (end of file)

YOU NEED
  Windows 10 or 11 (64-bit), Autodesk Revit 2025, about 600 MB of disk space, and internet during
  setup. Google Chrome is recommended. Unity and SOLIDWORKS are optional (without Unity the
  analyses give PDF charts instead of videos; SOLIDWORKS is only for Kinetics > Simulate).


1. INSTALL (about 5 minutes)
  1. Close Revit. Extract this ZIP (right-click > Extract All), then double-click the .exe.
  2. Windows may show a blue "Windows protected your PC" screen: the installer is not signed by a
     certificate Windows knows. Click "More info", then "Run anyway".
  3. Next > "I accept the agreement" > Next > keep both suggested folders > Install.
  4. Wait a minute or two. Setup registers the add-in with Revit (no DLL to copy by hand), installs
     Microsoft WebView2 and the Visual C++ runtime if they are missing, and prepares the database.
  5. Finish, with "Open the Sportify web app" ticked. Chrome opens it at http://localhost:5107/
     Later: Start menu > Sportify > Sportify web app.


2. SEE IT WORKING (about 2 minutes, Revit not needed)
  In the web app: Next > "I've used Sportify before" > "Load Goldbeck IFC Roof - Prebuilt Session".
    * "Low Roof, Sports"  - the team's layout on a real GOLDBECK roof: padel, ping pong, trampoline, tower
                            slide, mini-golf, planters, locker and bathroom modules
    * "High Roof, Garden" - a garden preset, with three saved iterations (planted, social, quiet)
  Then look at Combine (the layout on the real roof outline), Results and Compare.


3. IN REVIT
  1. Start Revit 2025. It asks about the Sportify add-in ("Digital Tools and Methods - Group of
     Sports and Gardens"): choose "Always Load".
  2. A "Sportify" tab appears in the ribbon, and the web app opens docked inside Revit.
  3. Open or start a project (Library\Templates\Sportify_EN.rte is ready: File > New > Project >
     Browse). Getting Started (first ribbon button) lists the steps and shows which are done.
  4. The main path: select a roof or slab > Push to Sportify > Everything; design it in the web app;
     Export Combined JSON; in Revit, Import Configuration; then the analyses and the documents.
  The full walkthrough: Start menu > Sportify > User guide (PDF).


WHERE THINGS ARE
  Start menu > Sportify: web app, web app inside Revit, your Sportify folder, library, user guide,
  Stop Sportify API, Uninstall.
  Your Sportify folder (default Documents\Sportify Workspace) collects everything you produce, in
  10 subfolders: Layouts, Sport fields, Garden, Physical analysis, Videos, Analysis reports,
  Schedules, Diagrams, Mechanical, Profile.


IF SOMETHING DOES NOT WORK
  * The web app shows no catalogue: start it from the Start menu (Sportify web app); it starts the
    local API first. Wait a few seconds and reload.
  * The docked pane in Revit is empty: WebView2 is missing - run setup again with internet, or use
    the web app in Chrome.
  * "The name already exists" when Revit starts: an older Sportify add-in is installed; run setup
    again and accept its offer to switch the old one off.
  * Logs: %APPDATA%\Sportify\logs  and the setup log  %TEMP%\Setup Log <date> #001.txt

UNINSTALL
  Windows Settings > Apps > Sportify > Uninstall. Your Sportify folder with your own files is kept.

SOURCE CODE
  https://github.com/nfe15rajab-bot/Sportify_Revit_and_API   (add-in, API, analyses, installer)
  https://github.com/nfe15rajab-bot/sportfify_goldbeck       (web app)


ONLY FOR THE TEAM'S DEVELOPMENT COMPUTERS
  If this computer has the developer build of Sportify in Revit, remove it first: close Revit,
  right-click Remove-Developer-Sportify.ps1 > "Run with PowerShell" (it moves the developer files
  into a backup folder in Documents; nothing is deleted; -DryRun only lists them).
