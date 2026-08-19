"""Take scene screenshots at chosen times of day / weather / spot."""
import sys
from playwright.sync_api import sync_playwright

URL = "http://127.0.0.1:5173/"
SHOTS = [
    ("day-kolam", 10.5, "clear", "kolam"),
    ("dusk-kolam", 18.2, "cloudy", "kolam"),
    ("day-tasik", 12.0, "rain", "tasik"),
    ("night-sungai", 21.0, "clear", "sungai"),
]

with sync_playwright() as pw:
    b = pw.chromium.launch(headless=True, args=[
        "--use-gl=angle", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"])
    p = b.new_page(viewport={"width": 1440, "height": 900})
    p.goto(URL, wait_until="load", timeout=60000)
    p.wait_for_function("() => !!window.PANCING", timeout=90000)
    p.wait_for_timeout(2000)

    for name, hour, weather, spot in SHOTS:
        p.evaluate("""([hour, weather, spot]) => {
          const P = window.PANCING;
          P.state.data.level = 20;
          P.state.data.money = 99999;
          P.state.checkSpotUnlocks();
          if (P.state.data.spot !== spot) P.state.travel(spot);
          P.world.hour = hour;
          P.world.weather = P.world.weather.id === weather ? P.world.weather
            : (window.__W ||= {}, Object.values(
                P.game.world.constructor.name ? {} : {}), P.world.weather);
          // Set weather directly from the table.
          const table = P.world.weather;
          const all = P.state && window.__WEATHER;
          P.world.nextWeatherIn = 9999;
          P.world.weatherBlend = 1;
          P.world.prevWeather = P.world.weather;
          const found = ({clear:{id:'clear',label:'Cerah',chance:.4,wind:.6,chop:.5,light:1,rain:0},
            cloudy:{id:'cloudy',label:'Mendung',chance:.32,wind:.9,chop:.8,light:.72,rain:0},
            rain:{id:'rain',label:'Hujan',chance:.2,wind:1.2,chop:1.2,light:.52,rain:.6},
            storm:{id:'storm',label:'Ribut',chance:.08,wind:2,chop:2,light:.35,rain:1}})[weather];
          P.world.weather = found;
        }""", [hour, weather, spot])
        # Let the sky/light/water lerp settle, then let waves run.
        p.wait_for_timeout(4500)
        p.screenshot(path=f"shots/scene-{name}.png")
        print("shot", name)

    b.close()
