# The mod's icon, from William's sketch: a pistol glowing orange on black, with the line arcing from it down to
# the hand in the bottom right corner. Writes icon-256.svg and icon-1024.svg into the folder given (see render.py).
import math, sys

SIZE = 256
# The gun is drawn pointing left (muzzle at x=0, top of the slide at y=0), then turned so the muzzle points
# down-left and the grip hangs toward the lower right, as in William's sketch.
ROT = -52.0
SCALE = 0.88
TX, TY = 8.0, 170.0

def world(x, y):
    a = math.radians(ROT)
    c, s = math.cos(a), math.sin(a)
    return (TX + SCALE * (c * x - s * y), TY + SCALE * (s * x + c * y))

def bez(p0, p1, p2, p3, t):
    u = 1 - t
    return tuple(u*u*u*a + 3*u*u*t*b + 3*u*t*t*c + t*t*t*d for a, b, c, d in zip(p0, p1, p2, p3))

def bez_d(p0, p1, p2, p3, t):
    u = 1 - t
    return tuple(3*u*u*(b-a) + 6*u*t*(c-b) + 3*t*t*(d-c) for a, b, c, d in zip(p0, p1, p2, p3))

def tapered(p0, p1, p2, p3, w0, w1, t0=0.0, t1=1.0, n=60, offset=0.0):
    left, right = [], []
    for i in range(n + 1):
        t = t0 + (t1 - t0) * i / n
        x, y = bez(p0, p1, p2, p3, t)
        dx, dy = bez_d(p0, p1, p2, p3, t)
        l = math.hypot(dx, dy) or 1
        nx, ny = -dy / l, dx / l
        x += nx * offset; y += ny * offset
        k = (t - t0) / ((t1 - t0) or 1)
        w = (w0 + (w1 - w0) * k) / 2
        left.append((x + nx * w, y + ny * w))
        right.append((x - nx * w, y - ny * w))
    pts = left + right[::-1]
    return "M" + " L".join(f"{x:.2f},{y:.2f}" for x, y in pts) + " Z"

# The line: from the back of the gun, where the slide meets the grip, arcing out and down to the hand in the
# bottom right corner.
start = world(203, 38)
P0 = start
P1 = (start[0] + 74, start[1] - 40)
P2 = (262, 100)
P3 = (234, 266)

gun = f'''
<g transform="translate({TX},{TY}) rotate({ROT}) scale({SCALE})">
  <!-- frame and grip -->
  <path id="body" d="
    M 4 4 L 8 0 L 14 0 L 15 -5 L 22 -5 L 23 0 L 186 0 L 190 -6 L 198 -6 L 200 0 L 204 2 L 214 14 L 212 30
    L 206 42 L 228 118 Q 230 130 218 133 L 186 140 Q 175 142 172 131 L 156 80 Q 152 70 143 70
    L 142 76 Q 140 86 128 86 L 108 86 Q 96 86 95 74 L 94 44 L 30 44 L 26 32 L 4 30 Z"/>
</g>'''

details = f'''
<g transform="translate({TX},{TY}) rotate({ROT}) scale({SCALE})" fill="none" stroke="#1c0b02" stroke-linecap="round">
  <!-- slide bottom edge, ejection port, serrations -->
  <path d="M 6 30 L 204 30" stroke-width="2.4"/>
  <rect x="96" y="7" width="44" height="12" rx="2" fill="#1c0b02" stroke="none"/>
  <path d="M 160 6 L 160 24 M 167 6 L 167 24 M 174 6 L 174 24 M 181 6 L 181 24" stroke-width="2.6"/>
  <!-- trigger guard hole and trigger -->
  <path d="M 104 46 L 135 46 L 134 70 Q 133 77 126 77 L 112 77 Q 105 77 104 70 Z" fill="#1c0b02" stroke="none"/>
  <path d="M 124 47 Q 128 58 121 68" stroke="#f0832a" stroke-width="3.4"/>
  <!-- grip panel -->
  <path d="M 168 60 L 202 52 L 218 116 Q 219 123 212 124 L 192 129 Q 186 130 184 124 Z" fill="#7a2c06" stroke="#1c0b02" stroke-width="2.4"/>
  <circle cx="190" cy="74" r="3.2" fill="#1c0b02" stroke="none"/>
  <circle cx="203" cy="112" r="3.2" fill="#1c0b02" stroke="none"/>
</g>'''

line_glow = tapered(P0, P1, P2, P3, 30, 12)
line_mid = tapered(P0, P1, P2, P3, 13, 5)
line_core = tapered(P0, P1, P2, P3, 5.5, 1.6)
streaks = "".join(
    f'<path d="{tapered(P0, P1, P2, P3, w, w*0.6, t0, t1, 30, off)}" fill="#ff8a2a" opacity="{op}"/>'
    for (t0, t1, off, w, op) in [(0.28, 0.52, -16, 2.2, 0.55), (0.42, 0.66, -25, 1.8, 0.4), (0.58, 0.78, -14, 1.6, 0.35)])
sparks = "".join(
    f'<circle cx="{bez(P0,P1,P2,P3,t)[0]+dx:.1f}" cy="{bez(P0,P1,P2,P3,t)[1]+dy:.1f}" r="{r}" fill="#ffd9a0" opacity="{op}"/>'
    for (t, dx, dy, r, op) in [(0.18, -4, 7, 1.6, 0.9), (0.33, 6, -3, 1.3, 0.8), (0.47, -6, 2, 1.1, 0.7), (0.62, 5, 4, 1.0, 0.6)])

svg = f'''<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" width="{{W}}" height="{{W}}" viewBox="0 0 {SIZE} {SIZE}">
<defs>
  <linearGradient id="metal" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="#ffb85c"/>
    <stop offset="0.55" stop-color="#ff8a1f"/>
    <stop offset="1" stop-color="#d9520a"/>
  </linearGradient>
  <radialGradient id="flare">
    <stop offset="0" stop-color="#fffaf0" stop-opacity="1"/>
    <stop offset="0.25" stop-color="#ffd38a" stop-opacity="0.95"/>
    <stop offset="0.6" stop-color="#ff8a1f" stop-opacity="0.35"/>
    <stop offset="1" stop-color="#ff6a00" stop-opacity="0"/>
  </radialGradient>
  <radialGradient id="ambient" cx="0.36" cy="0.32" r="0.55">
    <stop offset="0" stop-color="#3a1400" stop-opacity="0.9"/>
    <stop offset="1" stop-color="#000000" stop-opacity="0"/>
  </radialGradient>
  <filter id="blur8" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="8"/></filter>
  <filter id="blur3" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="3"/></filter>
  <filter id="blur1" x="-50%" y="-50%" width="200%" height="200%"><feGaussianBlur stdDeviation="1.2"/></filter>
</defs>
<rect width="{SIZE}" height="{SIZE}" fill="#000"/>
<rect width="{SIZE}" height="{SIZE}" fill="url(#ambient)"/>
<!-- glow around the gun, like the mod's glow on the selected object -->
<g filter="url(#blur8)" opacity="0.95">{gun.replace('id="body" ', 'fill="#ff6a00" stroke="#ff6a00" stroke-width="16" stroke-linejoin="round" ')}</g>
<g filter="url(#blur3)" opacity="0.9">{gun.replace('id="body" ', 'fill="#ff9a3a" stroke="#ffb060" stroke-width="5" stroke-linejoin="round" ')}</g>
<!-- the line -->
<path d="{line_glow}" fill="#ff6a00" opacity="0.55" filter="url(#blur8)"/>
<path d="{line_mid}" fill="#ff8f2e" opacity="0.9" filter="url(#blur1)"/>
<path d="{line_core}" fill="#fff1d6"/>
{streaks}
<!-- the gun -->
{gun.replace('id="body" ', 'fill="url(#metal)" stroke="#ffd59a" stroke-width="2.2" stroke-linejoin="round" ')}
{details}
{sparks}
<!-- the flare where the line leaves the gun -->
<circle cx="{start[0]:.1f}" cy="{start[1]:.1f}" r="26" fill="url(#flare)"/>
<circle cx="{start[0]:.1f}" cy="{start[1]:.1f}" r="5" fill="#fffaf0"/>
</svg>'''

out = sys.argv[1]
for w, name in [(256, "icon-256.svg"), (1024, "icon-1024.svg")]:
    open(f"{out}/{name}", "w", encoding="utf-8").write(svg.replace("{W}", str(w)))
print("start", start)
