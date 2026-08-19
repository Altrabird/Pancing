"""
Browser smoke test.

Loads the game in a real Chromium with WebGL, waits for boot, then drives an
actual cast->bite->hookset->fight->land cycle through the game's own input path
and reports what the renderer and simulation did. Fails loudly on any console
error, any WebGL context loss, or a simulation that never produces a catch.

    python tools/smoketest.py [--url http://127.0.0.1:5173/] [--headed]
"""

import sys
import json
import time
import argparse
from pathlib import Path

from playwright.sync_api import sync_playwright

OUT = Path(__file__).resolve().parent.parent / "shots"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="http://127.0.0.1:5173/")
    ap.add_argument("--headed", action="store_true")
    ap.add_argument("--seconds", type=float, default=45.0)
    args = ap.parse_args()

    OUT.mkdir(exist_ok=True)
    errors, warnings, page_errors = [], [], []

    with sync_playwright() as pw:
        browser = pw.chromium.launch(
            headless=not args.headed,
            args=[
                # Software GL so this works on a headless box with no GPU.
                "--use-gl=angle",
                "--use-angle=swiftshader",
                "--enable-unsafe-swiftshader",
                "--ignore-gpu-blocklist",
                "--enable-webgl",
            ],
        )
        page = browser.new_page(viewport={"width": 1440, "height": 900})

        page.on("console", lambda m: (
            errors.append(m.text) if m.type == "error"
            else warnings.append(m.text) if m.type == "warning" else None))
        page.on("pageerror", lambda e: page_errors.append(str(e)))

        print(f"loading {args.url} ...")
        page.goto(args.url, wait_until="load", timeout=60000)

        # Wait for the game to publish its debug handle.
        try:
            page.wait_for_function("() => !!window.PANCING", timeout=90000)
        except Exception:
            msg = page.evaluate("() => document.getElementById('boot-msg')?.textContent")
            print(f"FAILED to boot. Boot message: {msg!r}")
            for e in page_errors:
                print("  pageerror:", e)
            for e in errors[:20]:
                print("  console.error:", e)
            browser.close()
            sys.exit(1)

        print("booted. letting the scene settle ...")
        page.wait_for_timeout(2500)

        boot = page.evaluate("""() => {
          const P = window.PANCING, r = P.renderer.renderer;
          return {
            phase: P.game.telemetry().phase,
            fps: Math.round(P.loop.stats.fps),
            simMs: +P.loop.stats.simMs.toFixed(2),
            frameMs: +P.loop.stats.frameMs.toFixed(2),
            drawCalls: r.info.render.calls,
            triangles: r.info.render.triangles,
            programs: r.info.programs.length,
            textures: r.info.memory.textures,
            geometries: r.info.memory.geometries,
            contextLost: r.getContext().isContextLost(),
            spot: P.state.spot.name,
            clock: P.world.clockString(),
            hud: !!document.querySelector('.hud-top'),
            canvas: [r.domElement.width, r.domElement.height],
          };
        }""")
        print("\n--- boot state ---")
        for k, v in boot.items():
            print(f"  {k:14} {v}")

        page.screenshot(path=str(OUT / "01-scene.png"))

        # --- drive a full fishing cycle ------------------------------------
        # Speeds the sim up and installs an autopilot that uses the same public
        # API the player's input does, so this exercises the real code path.
        print("\nrunning autopilot fishing cycle ...")
        page.evaluate("""() => {
          const P = window.PANCING;
          P.loop.timeScale = 3.0;
          const log = { casts:0, bites:0, hooked:0, landed:0, lost:0, snaps:0, catches:[] };
          window.__log = log;
          P.bus.on(P.EV.BITE_ON, (p) => { if (p.phase === 'window') { log.bites++; setTimeout(() => P.game.strike(), 120); } });
          P.bus.on(P.EV.HOOKED, () => log.hooked++);
          P.bus.on(P.EV.LINE_SNAP, () => log.snaps++);
          P.bus.on(P.EV.HOOK_LOST, () => log.lost++);
          P.bus.on(P.EV.LANDED, (c) => { log.landed++; log.catches.push({
            name: c.species.name, cm: c.lengthCm, kg: c.massKg, s: c.fightSeconds, trophy: c.trophy }); });

          // Autopilot: hold the cast to the sweet spot, then manage tension.
          let hold = 0;
          window.__pilot = setInterval(() => {
            const t = P.game.telemetry();
            const inp = { reelAxis: 0, dragAxis: 0 };
            if (t.phase === 'ready') { P.game.beginCast(); hold = 0; log.casts++; }
            else if (t.phase === 'charging') { hold += 0.05; if (hold > 1.15) P.game.releaseCast(); }
            else if (t.phase === 'fight') {
              const L = t.rod.loadFrac;
              inp.reelAxis = L > 0.85 ? 0 : L > 0.55 ? 0.2 : 1.0;
              inp.dragAxis = L > 0.82 ? -1 : (t.rod.slipping && L < 0.55 ? 1 : 0);
            }
            window.__inp = inp;
          }, 50);

          // Feed the autopilot's intent into the real per-tick input object by
          // wrapping the game's update.
          const orig = P.game.update.bind(P.game);
          P.game.update = (dt, input) => orig(dt, window.__inp ?? input);
        }""")

        deadline = time.time() + args.seconds
        last = None
        while time.time() < deadline:
            page.wait_for_timeout(1500)
            log = page.evaluate("() => window.__log")
            if log != last:
                print(f"  casts={log['casts']} bites={log['bites']} hooked={log['hooked']} "
                      f"landed={log['landed']} snaps={log['snaps']} lost={log['lost']}")
                last = dict(log)
            if log["landed"] >= 3:
                break

        # Capture a shot mid-fight if we can catch one.
        state = page.evaluate("() => window.PANCING.game.telemetry().phase")
        if state == "fight":
            page.screenshot(path=str(OUT / "02-fight.png"))

        final = page.evaluate("""() => {
          const P = window.PANCING, r = P.renderer.renderer;
          clearInterval(window.__pilot);
          return {
            log: window.__log,
            fps: Math.round(P.loop.stats.fps),
            simMs: +P.loop.stats.simMs.toFixed(2),
            drawCalls: r.info.render.calls,
            triangles: r.info.render.triangles,
            contextLost: r.getContext().isContextLost(),
            level: P.state.data.level,
            money: P.state.data.money,
            records: Object.keys(P.state.data.records).length,
            saveOk: P.state.save(),
          };
        }""")

        # --- panels --------------------------------------------------------
        print("\nopening UI panels ...")
        panel_report = {}
        for tab in ["shop", "bag", "book", "quests", "travel"]:
            page.evaluate(f"() => window.PANCING.panels.show('{tab}')")
            page.wait_for_timeout(450)
            panel_report[tab] = page.evaluate(
                "() => document.querySelector('.panel-body')?.children.length ?? 0")
            page.screenshot(path=str(OUT / f"03-panel-{tab}.png"))
        page.evaluate("() => window.PANCING.panels.close()")

        page.wait_for_timeout(500)
        page.screenshot(path=str(OUT / "04-final.png"))

        browser.close()

    # --- report -------------------------------------------------------------
    log = final["log"]
    print("\n--- fishing cycle ---")
    print(f"  casts {log['casts']}  bite windows {log['bites']}  hooked {log['hooked']}  "
          f"landed {log['landed']}  snapped {log['snaps']}  lost {log['lost']}")
    for c in log["catches"]:
        print(f"    {c['name']:16} {c['cm']:6} cm  {c['kg']:7.3f} kg  {c['s']}s"
              + ("  TROPHY" if c["trophy"] else ""))

    print("\n--- final render state ---")
    for k in ("fps", "simMs", "drawCalls", "triangles", "contextLost", "level", "money", "records", "saveOk"):
        print(f"  {k:12} {final[k]}")

    print("\n--- panels (child nodes rendered) ---")
    for k, v in panel_report.items():
        print(f"  {k:8} {v}")

    print(f"\n--- console ---")
    print(f"  errors {len(errors)}   warnings {len(warnings)}   page errors {len(page_errors)}")
    for e in page_errors:
        print("   PAGEERROR:", e)
    for e in errors[:15]:
        print("   ERROR:", e[:300])
    for w in warnings[:8]:
        print("   warn:", w[:200])

    print(f"\nscreenshots -> {OUT}")

    problems = []
    if page_errors:
        problems.append(f"{len(page_errors)} uncaught page errors")
    if errors:
        problems.append(f"{len(errors)} console errors")
    if final["contextLost"]:
        problems.append("WebGL context lost")
    if log["landed"] == 0:
        problems.append("nothing was landed in the browser")
    if final["drawCalls"] == 0:
        problems.append("renderer issued no draw calls")
    if any(v == 0 for v in panel_report.values()):
        problems.append("a UI panel rendered empty")

    if problems:
        print("\nPROBLEMS: " + "; ".join(problems))
        sys.exit(1)
    print("\nSmoke test passed.")


if __name__ == "__main__":
    main()
