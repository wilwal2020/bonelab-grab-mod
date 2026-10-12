# Makes the mod's icon: writes the SVGs with make.py, then renders them to PNG with Edge, into art/ in the mod folder.
# Run: python render.py
import os, subprocess, sys, tempfile, time

EDGE = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
here = os.path.dirname(os.path.abspath(__file__))
art = os.path.normpath(os.path.join(here, "..", "..", "art"))
os.makedirs(art, exist_ok=True)
subprocess.run([sys.executable, os.path.join(here, "make.py"), art], check=True)

profiles = tempfile.mkdtemp(prefix="icon-edge-")
for n, name in ((256, "icon.png"), (1024, "icon-1024.png")):
    png = os.path.join(art, name)
    if os.path.exists(png):
        os.remove(png)
    svg = os.path.join(art, f"icon-{n}.svg").replace("\\", "/")
    # Edge writes the screenshot after it returns, so wait for the file.
    subprocess.run([EDGE, "--headless=new", "--disable-gpu", "--hide-scrollbars",
                    f"--user-data-dir={os.path.join(profiles, str(n))}", f"--window-size={n},{n}",
                    f"--screenshot={png}", f"file:///{svg}"], capture_output=True)
    for _ in range(60):
        if os.path.exists(png) and os.path.getsize(png) > 0:
            break
        time.sleep(0.5)
    time.sleep(0.5)
    print(png)
os.replace(os.path.join(art, "icon-256.svg"), os.path.join(art, "icon.svg"))
os.remove(os.path.join(art, "icon-1024.svg"))
