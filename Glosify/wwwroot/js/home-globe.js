import { landPoints } from './globe-land.js';

// Representative locations for the catalog's existing flag regions, [longitude, latitude].
// These orient the illustration; they are not a map of where a language is spoken.
export const regionCoordinates = Object.freeze({
    AM: [45, 40], AZ: [48, 40], BA: [18, 44], BD: [90, 24], BG: [25, 43],
    BR: [-52, -14], CN: [104, 35], CZ: [15, 50], DE: [10, 51], DK: [10, 56],
    EE: [26, 59], ES: [-4, 40], FI: [26, 64], FR: [2, 47], GB: [-2, 54],
    GE: [44, 42], GR: [23, 39], HK: [114.2, 22.3], HR: [16, 45], HU: [19, 47],
    ID: [118, -3], IL: [35, 31], IN: [79, 22], IR: [54, 32], IS: [-19, 65],
    IT: [12, 43], JP: [138, 37], KG: [75, 41], KR: [128, 36], KZ: [67, 48],
    LT: [24, 55], LV: [25, 57], MK: [22, 42], MM: [96, 21], MT: [14.4, 35.9],
    MY: [102, 4], NG: [8, 9], NL: [5, 52], NO: [10, 62], NP: [84, 28],
    NZ: [173, -41], PH: [123, 12], PL: [19, 52], RO: [25, 46], RS: [21, 44],
    RU: [60, 56], SA: [45, 24], SE: [15, 62], SI: [15, 46], SK: [20, 49],
    TH: [101, 15], TR: [35, 39], TZ: [35, -6], UA: [32, 49], UZ: [64, 41],
    VN: [106, 16], ZA: [25, -29],
});

export function resolveGlobeFocus(region) {
    const key = typeof region === 'string' ? region.trim().toUpperCase() : '';
    return Object.hasOwn(regionCoordinates, key) ? [...regionCoordinates[key]] : null;
}

const radians = Math.PI / 180;
const radius = 184;
const center = 220;

function vector([longitude, latitude]) {
    const lat = latitude * radians;
    const lon = longitude * radians;
    return [Math.cos(lat) * Math.sin(lon), Math.sin(lat), Math.cos(lat) * Math.cos(lon)];
}

function projector([longitude, latitude]) {
    const lon = longitude * radians;
    const lat = latitude * radians;
    const sinLon = Math.sin(lon), cosLon = Math.cos(lon);
    const sinLat = Math.sin(lat), cosLat = Math.cos(lat);
    return ([x, y, z]) => {
        const depth = x * sinLon + z * cosLon;
        return {
            x: center + radius * (x * cosLon - z * sinLon),
            y: center - radius * (y * cosLat - depth * sinLat),
            visible: y * sinLat + depth * cosLat > 0,
        };
    };
}

export function projectGlobePoint(point, focus) {
    return projector(focus)(vector(point));
}

const land = landPoints.map(vector);
const grid = [];
for (let lat = -60; lat <= 60; lat += 30) {
    grid.push(Array.from({ length: 181 }, (_, i) => vector([-180 + i * 2, lat])));
}
for (let lon = -180; lon < 180; lon += 30) {
    grid.push(Array.from({ length: 91 }, (_, i) => vector([lon, -90 + i * 2])));
}

export function renderGlobePaths(focus) {
    const project = projector(focus);
    // A single round-capped path draws the land dots, avoiding thousands of DOM nodes.
    const landPath = land.map(point => {
        const p = project(point);
        return p.visible ? `M${p.x.toFixed(3)},${p.y.toFixed(3)}h.1` : '';
    }).join('');
    const gridPath = grid.map(line => {
        let drawing = false;
        return line.map(point => {
            const p = project(point);
            if (!p.visible) { drawing = false; return ''; }
            const command = drawing ? 'L' : 'M';
            drawing = true;
            return `${command}${p.x.toFixed(3)},${p.y.toFixed(3)}`;
        }).join('');
    }).join('');
    return { landPath, gridPath };
}

export function mountGlobe(element, environment = window) {
    const doc = element.ownerDocument;
    const landElement = element.querySelector('[data-globe-land]');
    const gridElement = element.querySelector('[data-globe-grid]');
    const marker = element.querySelector('[data-globe-marker]');
    const fallback = element.querySelector('[data-globe-fallback]');
    const focus = resolveGlobeFocus(element.dataset.region);
    const orientation = focus ?? [12, 20];
    const motion = environment.matchMedia('(prefers-reduced-motion: reduce)');
    let frame = null;
    let lastTime = null;
    let elapsed = 0;
    let inView = true;
    let disposed = false;

    function draw() {
        const sway = motion.matches ? 0 : Math.sin(elapsed / 10000) * 5;
        const current = [orientation[0] + sway, orientation[1]];
        const { landPath, gridPath } = renderGlobePaths(current);
        landElement.setAttribute('d', landPath);
        gridElement.setAttribute('d', gridPath);
        marker.hidden = !focus;
        marker.style.display = focus ? '' : 'none';
        if (focus) {
            const point = projectGlobePoint(focus, current);
            marker.setAttribute('transform', `translate(${point.x.toFixed(3)} ${point.y.toFixed(3)})`);
        }
        fallback.style.display = 'none';
    }

    function tick(time) {
        frame = null;
        if (disposed || doc.hidden || !inView || motion.matches) return;
        // Follow the display refresh rate rather than stepping between 30 Hz redraws.
        if (lastTime !== null) elapsed += time - lastTime;
        lastTime = time;
        draw();
        frame = environment.requestAnimationFrame(tick);
    }

    function syncMotion() {
        if (frame !== null) environment.cancelAnimationFrame(frame);
        frame = null;
        lastTime = null;
        const paused = doc.hidden || !inView || motion.matches;
        element.classList.toggle('is-paused', paused);
        if (motion.matches) draw();
        if (!paused && !disposed) frame = environment.requestAnimationFrame(tick);
    }

    draw();
    doc.addEventListener('visibilitychange', syncMotion);
    motion.addEventListener('change', syncMotion);
    const observer = environment.IntersectionObserver ? new environment.IntersectionObserver(entries => {
        inView = entries[0].isIntersecting;
        syncMotion();
    }) : null;
    observer?.observe(element);
    syncMotion();
    return () => {
        disposed = true;
        if (frame !== null) environment.cancelAnimationFrame(frame);
        observer?.disconnect();
        doc.removeEventListener('visibilitychange', syncMotion);
        motion.removeEventListener('change', syncMotion);
    };
}

if (typeof document !== 'undefined') {
    document.querySelectorAll('[data-home-globe]').forEach(element => mountGlobe(element));
}
