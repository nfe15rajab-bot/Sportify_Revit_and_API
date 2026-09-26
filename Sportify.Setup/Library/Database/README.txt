The Sportify reference database
===============================

What it is
  The catalogue behind the web app: sport fields and their variants, garden and roof build-ups, materials, providers, prices, plant palettes, furniture and the parameters of
  the analyses. It is a SQLite file (reference.db) and needs no database server.

Where it lives
  <install folder>\api\reference.db, made by the local API (Sportify.Api.exe) the first time it runs. Setup starts the API once, so the file exists when setup finishes.
  It is built from the data shipped inside the API; nothing has to be imported by hand. If a later version of Sportify needs new tables or columns, the API adds them in place
  and keeps what you entered in the web app's Data tab; when something cannot be added in place it makes a backup of the old file first.

Reset it
  Close the web app, run "Stop Sportify API" (Start menu), delete reference.db from the folder above, and open the web app again: it is rebuilt from the shipped data.

Look at it
  Any SQLite viewer opens the file. Change the catalogue in the web app's Data tab rather than in the file.
