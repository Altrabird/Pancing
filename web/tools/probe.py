"""Poll every frame during a fight and report the first non-finite value."""
import sys
from playwright.sync_api import sync_playwright

URL = sys.argv[1] if len(sys.argv) > 1 else "http://127.0.0.1:5173/"

with sync_playwright() as pw:
    b = pw.chromium.launch(headless=True, args=[
        "--use-gl=angle", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"])
    p = b.new_page(viewport={"width": 1280, "height": 800})
    p.goto(URL, wait_until="load", timeout=60000)
    p.wait_for_function("() => !!window.PANCING", timeout=90000)
    p.wait_for_timeout(1200)

    p.evaluate("""() => {
      const P = window.PANCING;
      window.__bad = null;
      const fin = (v) => typeof v === 'number' && Number.isFinite(v);
      const probe = () => {
        if (!window.__bad) {
          const r = P.renderer, g = P.game;
          const tip = r.rodVisual?.tipWorld, fp = r.fishVisual?.group?.position;
          const lp = r.lureVisual?.group?.position;
          const vals = {
            'rod.tension': g.rod.tension,
            'rod.lineOut': g.rod.lineOut,
            'rod.bend': g.rod.bend,
            'fish.dist': g.fish?.dist,
            'fish.depth': g.fish?.depth,
            'fish.lateral': g.fish?.lateral,
            'fish.pull': g.fish?.pull,
            'fish.velAway': g.fish?.velAway,
            'bedDepth': g.bedDepth,
            'tip.x': tip?.x, 'tip.y': tip?.y, 'tip.z': tip?.z,
            'fishPos.x': fp?.x, 'fishPos.y': fp?.y, 'fishPos.z': fp?.z,
            'lurePos.x': lp?.x, 'lurePos.y': lp?.y, 'lurePos.z': lp?.z,
            'rodRot.z': r.rodVisual?.group?.rotation?.z,
            'waterH(fish)': g.fish ? r.water.heightAt(g.fish.lateral, g.fish.dist) : 0,
            'castPos.x': g.cast.pos.x, 'castPos.z': g.cast.pos.z,
          };
          const bad = Object.entries(vals).filter(([k,v]) => v !== undefined && v !== null && !fin(v));
          if (bad.length) {
            window.__bad = { phase: g.phase, fishState: g.fish?.state,
                             bad: bad.map(([k])=>k), all: vals };
          }
        }
        requestAnimationFrame(probe);
      };
      requestAnimationFrame(probe);

      // Drive fights.
      P.game._finishCast('reset');
      let hold = 0;
      window.__inp = { reelAxis: 0, dragAxis: 0 };
      const orig = P.game.update.bind(P.game);
      P.game.update = (dt) => orig(dt, window.__inp);
      setInterval(() => {
        const t = P.game.telemetry(); const i = { reelAxis: 0, dragAxis: 0 };
        if (t.phase === 'ready') { P.game.beginCast(); hold = 0; }
        else if (t.phase === 'charging') { hold += 0.05; if (hold > 1.1) P.game.releaseCast(); }
        else if (t.phase === 'fight') {
          const L = t.rod.loadFrac;
          i.reelAxis = L > 0.85 ? 0 : L > 0.55 ? 0.2 : 1.0;
          i.dragAxis = L > 0.82 ? -1 : (t.rod.slipping && L < 0.55 ? 1 : 0);
        }
        window.__inp = i;
      }, 50);
      P.bus.on(P.EV.BITE_ON, (e) => { if (e.phase === 'window') setTimeout(() => P.game.strike(), 130); });
      P.loop.timeScale = 4;
    }""")

    for i in range(30):
        p.wait_for_timeout(2000)
        bad = p.evaluate("() => window.__bad")
        if bad:
            print("FIRST NON-FINITE at phase:", bad["phase"], "fish state:", bad["fishState"])
            print("bad keys:", bad["bad"])
            print("\nall values at that moment:")
            for k, v in bad["all"].items():
                print(f"   {k:16} {v}")
            break
        ph = p.evaluate("() => window.PANCING.game.phase")
        print(f"  ...{ph}")
    else:
        print("no non-finite values observed")
    b.close()
