# Dialtone — brand guide

**Proposed display name:** Dialtone
**Tagline:** *A rotary phone with a modern soul.*

## Why this name

**Dialtone** is the sound a working phone makes before anything else happens — the proof of life
for a voice service. One word, instantly telephonic, and it names the service rather than the
hardware (the repo name already covers the hardware).

**Alternates considered:** *Rotary* (adjective posing as a name), *Exchange* (telephony-correct,
collides with Microsoft), *Party Line* (era-correct, implies multi-user).

## The mark

A rotary dial face: ten finger holes, the finger stop picked out in red — the one touch of color,
where your finger goes. Ivory on bakelite black, the material palette of the real object.

## Palette

| Color | Hex | Role |
|---|---|---|
| Bakelite | `#1C1C1C` | Background / primary brand color |
| Ivory | `#F5F0E6` | Dial, text on dark |
| Rotary Red | `#C0392B` | Finger stop, accents, alerts |

## Voice

Era voice, used sparingly: "place a call", "the line is busy". Keep technical docs modern; save
the nostalgia for user-facing surfaces.

## Files in this directory

| File | Use |
|---|---|
| `logo.svg` | Full lockup (mark + wordmark + tagline) for README headers and docs |
| `favicon.svg` | Square app mark, scales from 16px to full size |
| `favicon.ico` | Legacy multi-size favicon (16/32/48) for browsers that want `.ico` |
| `favicon-32.png` | 32px PNG favicon |
| `apple-touch-icon.png` | 180px iOS home-screen icon |
| `icon-512.png` | Large raster for app manifests, social cards, stores |

### Wiring the favicon into a web page

```html
<link rel="icon" href="/branding/favicon.svg" type="image/svg+xml">
<link rel="icon" href="/branding/favicon.ico" sizes="16x16 32x32 48x48">
<link rel="apple-touch-icon" href="/branding/apple-touch-icon.png">
```

### README header

```markdown
<p align="center"><img src="branding/logo.svg" alt="Dialtone" width="520"></p>
```

## Typography

Wordmark: **Montserrat Bold** (falls back to Segoe UI / system sans). Body text: the platform
default sans. For code-adjacent surfaces, any monospace at hand — the brand doesn't pin one.

The logo's wordmark is live SVG text, so it renders with whatever sans is installed; if you want
it pixel-identical everywhere, convert the text to outlines in any SVG editor and re-save.

## Dark and light backgrounds

The tile carries its own background, so both `logo.svg` and `favicon.svg` work unchanged on
light or dark pages. The wordmark in `logo.svg` is dark ink — on a dark page, either rely on the
tile alone (use `favicon.svg`) or restyle the two `<text>` fills to `#F0F2F5`.

---
*Generated as a proposal — names, colors, and marks are suggestions to accept, tweak, or reject.*
