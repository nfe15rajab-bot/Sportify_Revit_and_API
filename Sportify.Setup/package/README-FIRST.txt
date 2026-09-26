SPORTIFY {{VERSION}} - test package for Autodesk Revit 2025
================================================================================
Digital Tools and Methods - Group of Sports and Gardens, TH OWL. Open source, for
educational purposes, still under testing.

IN THIS ZIP
  Sportify-Setup-{{VERSION}}-Revit2025.exe   the installer: ONE file with the Revit add-in, the web app,
                                              its local API and database, and a library of templates,
                                              families and documentation
  Sportify-Setup-{{VERSION}}-Revit2025.exe.sha256   checksum, to check the download (optional)
  Remove-Developer-Sportify.ps1               only for a computer that has the DEVELOPER version of
                                              Sportify in Revit (see step 0)
  LICENSE_AGREEMENT.txt                       what setup will ask you to accept
  README-FIRST.txt                            this file


STEP 0 - ONLY IF THIS COMPUTER HAS THE DEVELOPER VERSION OF SPORTIFY IN REVIT
  1. Close Revit.
  2. Right-click Remove-Developer-Sportify.ps1 > "Run with PowerShell". It lists what it would
     move out of Revit's Addins folder, then asks once. Nothing is deleted: everything goes to a
     folder "Sportify-developer-backup-..." in your Documents (move it back to undo).
     To only look first, open PowerShell and run:  .\Remove-Developer-Sportify.ps1 -DryRun
  3. Start Revit once: the "Sportify" tab must be gone. Close Revit.
  (A Sportify that was installed with the installer is removed in Windows Settings > Apps.)


STEP 1 - INSTALL
  1. Extract the ZIP (right-click > Extract All), then double-click the .exe.
  2. Windows may show a blue "Windows protected your PC" screen: the installer is not code-signed
     yet. Click "More info", then "Run anyway".
  3. Welcome > Next. License: choose "I accept the agreement" > Next.
  4. Install folder: keep the suggestion (or choose a folder you can write to, not Program Files)
     > Next.
  5. Your Sportify folder (your layouts, charts, videos, reports go here): keep or choose > Next.
  6. Install, and wait: it copies the files, registers the add-in with Revit, installs Microsoft's
     WebView2 and Visual C++ runtime if they are missing (needs internet, may ask for permission)
     and starts the local API once to prepare its database. This takes a minute or two.
  7. Last page: keep "Open the Sportify web app" ticked > Finish. Chrome opens the web app.
     (Or tick "Open Revit ... docked inside it instead" to see the app inside Revit.)


STEP 2 - WHAT TO CHECK (about 15 minutes) - write down anything that differs
  [ ] Chrome opened the Sportify web app (address http://localhost:5107/) and the tabs load
      (Site, Structure, Sport, Combine, Analysis, Post Analysis, Data ...)
  [ ] Windows Start menu > Sportify has: Sportify web app, Sportify folder (my files), Sportify
      library, User guide, Stop Sportify API, Uninstall Sportify
  [ ] Start Revit 2025. Revit asks about an add-in from an unknown publisher: it names
      "Digital Tools and Methods - Group of Sports and Gardens". Choose "Always Load".
  [ ] The ribbon has a "Sportify" tab with six panels and the web app opens docked inside Revit
      (the "Open Sportify App" button shows it again)
  [ ] Nothing says "The name already exists" or "Revit cannot run the external application"
  [ ] Your Sportify folder exists with 9 subfolders (Layouts, Sport fields, Garden, Physical
      analysis, Videos, Analysis reports, Schedules, Diagrams, Mechanical); Ribbon > Open Sportify
      Folder opens it
  [ ] The install folder has a Library folder: Templates (2 .rte), Families (.rfa), Worksets,
      Documentation (the User guide PDF opens), Database
  [ ] In Revit: File > New > Project > Browse > Library\Templates\Sportify_EN.rte opens a project
  [ ] Try the User guide's first steps (Step 1 to 4: open the app, push a roof, design, import)


STEP 3 - UNINSTALL TEST
  Windows Settings > Apps > Sportify > Uninstall. Then: the Sportify tab is gone from Revit, the
  install folder is gone, your Sportify folder with your own files is still there.


IF SOMETHING GOES WRONG - send these
  * a screenshot of the message, and which step you were on
  * the setup log:      %TEMP%\Setup Log <date> #001.txt        (open Explorer, type %TEMP% in the bar)
  * Sportify's log:     %APPDATA%\Sportify\logs
  * the Windows version, whether Revit 2025 is installed, whether Chrome is the default browser

  The web app can be stopped and started from the Start menu (Stop Sportify API / Sportify web app).
  The full user guide is in the Start menu: Sportify > User guide.
