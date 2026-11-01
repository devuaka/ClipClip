// Where the download buttons point. Leave empty to serve the files from ./downloads beside this
// page. Set a full URL (ending in /) when the files live elsewhere, such as a GitHub release.
const DOWNLOAD_BASE = 'https://github.com/coolfamesguy-svg/ClipClip/releases/latest/download/';

// Where the footer's source code link points.
const SOURCE_URL = 'https://github.com/coolfamesguy-svg/ClipClip';

const DOWNLOADS = { setup: 'ClipClip-win-Setup.exe', portable: 'ClipClip-win-Portable.zip' };

if (SOURCE_URL) document.getElementById('source-link').href = SOURCE_URL;

if (DOWNLOAD_BASE) {
  document.querySelectorAll('[data-download]').forEach((link) => {
    link.href = DOWNLOAD_BASE + DOWNLOADS[link.dataset.download];
  });
}

// The replay rail: a waveform that keeps moving, like the buffer in the app.
const canvas = document.getElementById('rail-canvas');
const context = canvas.getContext('2d');
const calm = window.matchMedia('(prefers-reduced-motion: reduce)').matches;

const BAR_WIDTH = 4;
const BAR_GAP = 3;
const BAR_STEP = BAR_WIDTH + BAR_GAP;
const SECONDS_ACROSS = 30;
const CLIP_DEBOUNCE_MS = 750;

let bars = [];
let offset = 0;
let width = 0;
let height = 0;
let seed = 0;
let lastFrame = 0;
let lastClip = 0;

// Bursts over a quiet bed read as game audio. Deterministic, so the rest state always looks full.
function nextBar() {
  seed += 1;
  const swell = Math.abs(Math.sin(seed * 0.05)) * Math.abs(Math.sin(seed * 0.013 + 1));
  const grit = (Math.sin(seed * 12.9898) * 43758.5453) % 1;
  return 0.1 + Math.min(0.85, swell * 0.72 + Math.abs(grit) * 0.2);
}

function resize() {
  const ratio = Math.min(window.devicePixelRatio || 1, 2);
  const box = canvas.getBoundingClientRect();
  width = box.width;
  height = box.height;
  canvas.width = Math.round(width * ratio);
  canvas.height = Math.round(height * ratio);
  context.setTransform(ratio, 0, 0, ratio, 0, 0);
  const needed = Math.ceil(width / BAR_STEP) + 2;
  while (bars.length < needed) bars.push(nextBar());
  bars = bars.slice(-needed);
  draw();
}

function draw() {
  context.clearRect(0, 0, width, height);
  const middle = (height - 22) / 2;
  const reach = middle - 8;
  const gradient = context.createLinearGradient(0, 0, width, 0);
  gradient.addColorStop(0, 'rgba(255, 61, 90, 0.22)');
  gradient.addColorStop(0.75, '#ff3d5a');
  gradient.addColorStop(1, '#ff8a3d');
  context.fillStyle = gradient;

  bars.forEach((level, index) => {
    const x = index * BAR_STEP - offset;
    const size = Math.max(2, level * reach);
    context.fillRect(x, middle - size, BAR_WIDTH, size * 2);
  });

  context.fillStyle = '#ff3d5a';
  context.fillRect(width - 2, 0, 2, height);
}

function frame(now) {
  const elapsed = Math.min(now - lastFrame, 100);
  lastFrame = now;
  // Bars cross the whole rail in 30 real seconds, the same pace as the buffer itself.
  offset += (width / SECONDS_ACROSS) * (elapsed / 1000);
  while (offset >= BAR_STEP) {
    offset -= BAR_STEP;
    bars.shift();
    bars.push(nextBar());
  }
  draw();
  requestAnimationFrame(frame);
}

function clip() {
  const now = Date.now();
  if (now - lastClip < CLIP_DEBOUNCE_MS) return;
  lastClip = now;

  const flash = document.getElementById('rail-window');
  flash.classList.remove('flash');
  void flash.offsetWidth;
  flash.classList.add('flash');

  document.getElementById('saved-empty')?.remove();
  const saved = document.getElementById('saved');
  const stamp = new Date().toLocaleTimeString([], { hour12: false }).replaceAll(':', '-');
  const chip = document.createElement('div');
  chip.className = 'chip';
  chip.textContent = `Clip_${stamp}.mp4 `;
  const note = document.createElement('span');
  note.textContent = '30s';
  chip.append(note);
  saved.prepend(chip);
  while (saved.children.length > 3) saved.lastElementChild.remove();
}

document.getElementById('rail-clip').addEventListener('click', clip);
window.addEventListener('keydown', (event) => {
  if (event.key !== 'F8' || event.repeat) return;
  event.preventDefault();
  clip();
});

window.addEventListener('resize', resize);
resize();
if (!calm) {
  requestAnimationFrame((now) => {
    lastFrame = now;
    requestAnimationFrame(frame);
  });
}
