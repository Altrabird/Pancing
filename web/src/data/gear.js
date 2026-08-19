/**
 * Gear tables.
 *
 * Rod / reel / line / lure stats feed directly into the tension solver in
 * physics/rod.js. The numbers are in real-ish units so the physics stays
 * intuitive to tune:
 *
 *   power     N   force at the rod tip that produces full bend
 *   stiffness N/m spring rate of the blank; higher = less shock absorption
 *   test      kg  line breaking strain (converted to N by the solver)
 *   stretch   -   fraction of length the line yields before load builds
 *   drag      N   maximum clutch force before the spool slips
 *   retrieve  m/s bare line speed at zero load
 */

export const RODS = [
  {
    id: 'rod_buluh', name: 'Joran Buluh', desc: 'Batang buluh potong sendiri. Lembut, memaafkan, lemah.',
    price: 0, level: 1, power: 22, stiffness: 42, length: 2.4, castPower: 0.62,
    sensitivity: 0.45, tipColor: '#c9a86b',
  },
  {
    id: 'rod_fiber', name: 'Joran Fiber', desc: 'Fiberglass murah kedai. Serba boleh, tiada kelemahan besar.',
    price: 180, level: 2, power: 38, stiffness: 68, length: 2.7, castPower: 0.80,
    sensitivity: 0.62, tipColor: '#4c5560',
  },
  {
    id: 'rod_graphite', name: 'Joran Grafit', desc: 'Ringan dan sensitif. Getaran halus pun terasa.',
    price: 720, level: 5, power: 55, stiffness: 105, length: 2.9, castPower: 0.92,
    sensitivity: 0.86, tipColor: '#2b2f35',
  },
  {
    id: 'rod_carbon', name: 'Joran Karbon HM', desc: 'Modulus tinggi. Kuat, tegas, tidak memaafkan silap.',
    price: 2400, level: 9, power: 82, stiffness: 158, length: 3.1, castPower: 1.05,
    sensitivity: 0.95, tipColor: '#1b1d21',
  },
  {
    id: 'rod_kelah', name: 'Joran Pemburu Kelah', desc: 'Dibina untuk satu tujuan sahaja: ikan sungai gergasi.',
    price: 7800, level: 13, power: 118, stiffness: 132, length: 3.3, castPower: 1.12,
    sensitivity: 1.0, tipColor: '#5a2f24',
  },
];

export const REELS = [
  {
    id: 'reel_tangan', name: 'Gulung Tangan', desc: 'Tiada klac. Tarikan ikan terus ke tangan anda.',
    price: 0, level: 1, drag: 33, retrieve: 0.55, ratio: 3.2, dragSmooth: 0.35,
  },
  {
    id: 'reel_spin2000', name: 'Spinning 2000', desc: 'Klac asas. Cukup untuk ikan kolam.',
    price: 220, level: 2, drag: 62, retrieve: 0.72, ratio: 5.0, dragSmooth: 0.58,
  },
  {
    id: 'reel_spin4000', name: 'Spinning 4000', desc: 'Klac lebih licin, gear lebih laju.',
    price: 950, level: 5, drag: 84, retrieve: 0.88, ratio: 5.6, dragSmooth: 0.74,
  },
  {
    id: 'reel_bc', name: 'Baitcaster Pro', desc: 'Kuasa mentah untuk menarik ikan keluar dari reba.',
    price: 3100, level: 9, drag: 152, retrieve: 1.02, ratio: 7.1, dragSmooth: 0.86,
  },
  {
    id: 'reel_sw', name: 'Reel Air Masin', desc: 'Klac karbon berendam. Licin walau di bawah beban penuh.',
    price: 8600, level: 13, drag: 300, retrieve: 1.14, ratio: 6.2, dragSmooth: 0.97,
  },
];

export const LINES = [
  {
    id: 'line_mono8', name: 'Mono 8lb', desc: 'Regangan tinggi menyerap hentakan, tetapi lambat memberi isyarat.',
    price: 0, level: 1, test: 3.6, stretch: 0.16, visibility: 0.55, abrasion: 0.45,
  },
  {
    id: 'line_mono15', name: 'Mono 15lb', desc: 'Lebih tebal, lebih selamat, ikan lebih nampak.',
    price: 60, level: 2, test: 6.8, stretch: 0.15, visibility: 0.72, abrasion: 0.55,
  },
  {
    id: 'line_fluoro', name: 'Fluorocarbon 12lb', desc: 'Hampir halimunan dalam air. Ikan berhati-hati pun sambar.',
    price: 340, level: 4, test: 5.4, stretch: 0.09, visibility: 0.18, abrasion: 0.70,
  },
  {
    id: 'line_braid30', name: 'Braid 30lb', desc: 'Hampir sifar regangan. Setiap getaran sampai, setiap silap juga.',
    price: 780, level: 7, test: 13.6, stretch: 0.02, visibility: 0.80, abrasion: 0.62,
  },
  {
    id: 'line_braid65', name: 'Braid 65lb', desc: 'Tali tarik gergasi. Joran anda akan patah dahulu.',
    price: 2600, level: 11, test: 29.5, stretch: 0.02, visibility: 0.88, abrasion: 0.85,
  },
];

/**
 * Lures. `action` drives the bite FSM: how much retrieving the lure adds to
 * attraction, and how much it risks spooking a cautious fish.
 */
export const LURES = [
  {
    id: 'worm', name: 'Cacing', desc: 'Umpan sejagat. Semua ikan kenal cacing.',
    price: 0, level: 1, consumable: true, stock: Infinity,
    sink: 0.55, action: 0.15, noise: 0.10, spook: 0.05, sizeBias: 0.0,
  },
  {
    id: 'dough', name: 'Umpan Doh', desc: 'Doh berperisa untuk ikan pemakan tumbuhan.',
    price: 4, level: 1, consumable: true, stock: 40,
    sink: 0.45, action: 0.10, noise: 0.05, spook: 0.03, sizeBias: 0.05,
  },
  {
    id: 'pellet', name: 'Pelet', desc: 'Pelet ternakan. Ikan kolam ketagih.',
    price: 5, level: 1, consumable: true, stock: 40,
    sink: 0.60, action: 0.08, noise: 0.08, spook: 0.02, sizeBias: 0.0,
  },
  {
    id: 'shrimp', name: 'Udang Hidup', desc: 'Umpan dasar terbaik untuk ikan bermisai.',
    price: 12, level: 3, consumable: true, stock: 25,
    sink: 0.80, action: 0.22, noise: 0.06, spook: 0.02, sizeBias: 0.12,
  },
  {
    id: 'fruit', name: 'Buah Sawit', desc: 'Rahsia pemburu kelah dan jelawat.',
    price: 20, level: 4, consumable: true, stock: 20,
    sink: 0.30, action: 0.05, noise: 0.04, spook: 0.01, sizeBias: 0.22,
  },
  {
    id: 'spinner', name: 'Spinner', desc: 'Bilah berputar. Kilauan dan getaran menarik pemangsa.',
    price: 45, level: 3, consumable: false, stock: Infinity,
    sink: 0.50, action: 0.70, noise: 0.55, spook: 0.30, sizeBias: 0.15,
  },
  {
    id: 'minnow', name: 'Minnow', desc: 'Gewang menyelam meniru anak ikan.',
    price: 90, level: 5, consumable: false, stock: Infinity,
    sink: 0.65, action: 0.62, noise: 0.35, spook: 0.22, sizeBias: 0.20,
  },
  {
    id: 'popper', name: 'Popper', desc: 'Letupan permukaan. Bising, ganas, seronok.',
    price: 130, level: 7, consumable: false, stock: Infinity,
    sink: 0.05, action: 0.85, noise: 0.90, spook: 0.42, sizeBias: 0.25,
  },
  {
    id: 'frog', name: 'Katak Getah', desc: 'Melintas atas rumpai. Umpan wajib haruan dan toman.',
    price: 160, level: 8, consumable: false, stock: Infinity,
    sink: 0.02, action: 0.75, noise: 0.60, spook: 0.28, sizeBias: 0.30,
  },
];

export const GEAR_TABLES = { rod: RODS, reel: REELS, line: LINES, lure: LURES };

export const GEAR_BY_ID = {};
for (const [slot, table] of Object.entries(GEAR_TABLES)) {
  for (const item of table) GEAR_BY_ID[item.id] = { ...item, slot };
}

/** Everything the player starts with, free of charge. */
export const STARTER_KIT = {
  rod: 'rod_buluh',
  reel: 'reel_tangan',
  line: 'line_mono8',
  lure: 'worm',
};
