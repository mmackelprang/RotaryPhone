# Tokens

**No new colour, font, or spacing tokens are introduced.** Everything below assembles existing tokens from `D:\prj\RTest\RTest\src\Radio.Web\wwwroot\css\design-system.css` (`:root`, §2). This file exists only to (a) certify zero-new-token compliance and (b) fully specify the one genuinely-new visual primitive — message bubbles — from existing tokens, so the Planner/Builder don't have to invent values and Polisher has an exact reference.

If any value below cannot be expressed with an existing token, it is called out explicitly. There is one such case (bubble max-width), noted at the end, and it is a layout dimension, not a colour/brand token.

---

## Reused tokens (the full set this design touches)

| Token | Value | Used here for |
|---|---|---|
| `--surface-base` | `#0D0D0F` | Panel backgrounds |
| `--surface-raised` | `#141416` | **Inbound bubble**, cards, list rows |
| `--surface-inset` | `#0A0A0C` | Compose-bar background, rail bg |
| `--surface-overlay` | `#1A1A1D` | Button hover |
| `--surface-separator` | `#1F1F22` | Hairline borders, bubble border, scrubber track |
| `--accent-primary` | `#5CD4E8` | Cyan — selected thread, unread dots, scrubber fill, **outbound bubble accent** |
| `--accent-surface` | `rgba(92,212,232,0.08)` | **Outbound bubble background**, active list row bg |
| `--accent-dim` | `rgba(92,212,232,0.06)` | Active tab bg |
| `--signal-amber` | `#F0A830` | `.nav-badge` unread count, auth-reconnecting banner accent |
| `--signal-green` | `#4ADE80` | Send-success check (optional) |
| `--signal-red` | `#F87171` | **Failed bubble** edge/glyph, error states |
| `--signal-blue` | `#60A5FA` | Info toast accent (Radzen handles) |
| `--text-high` | `#F0EFF4` | Bubble text, primary list text |
| `--text-medium` | `#B5BCC9` | Secondary text, read rows, sub-lines |
| `--text-low` | `#4B5563` | Timestamps, sent-check glyph, segment counter |
| `--text-inverse` | `#0D0D0F` | Text on amber `.nav-badge` |
| `--font-body` | Inter stack | Bubble + body text |
| `--font-mono` | JetBrains Mono stack | Timestamps, durations, phone numbers, segment counter, pills |
| `--font-led` | Orbitron stack | (not needed here; voicemail duration uses mono, not LED, to match list-meta) |
| `--sp-2`…`--sp-4` | `8/12/16px` | Bubble padding, gaps |
| `--touch-min` / `--touch-preferred` | `48 / 56px` | Send button, row min-height, scrubber hit area |
| `--anim-duration-normal` / `--anim-ease-*` | `200ms` / eases | Bubble-in, list-add, accordion |

---

## §Bubbles — message bubble spec (NEW primitive, existing tokens only)

Proposed new CSS, to be added to RadioConsole's `design-system.css` **§Ph (Phone Page Surface)** block (same section the phone cards/pills live in), following the established `phone-`/`§Ph` naming discipline from the phone-page handoff. Names are prefixed to avoid collisions, exactly as that handoff did (`.phone-card`, `.phone-pill`, etc.).

```css
/* -- §Ph  Text message bubbles --------------------------------------------- */

.msg-list {
  display: flex;
  flex-direction: column;
  gap: var(--sp-2);                 /* 8px between bubbles */
  padding: var(--sp-3) var(--sp-4); /* 12 / 16 */
  overflow-y: auto;
  min-height: 0;
}

.msg-bubble {
  max-width: 72%;                    /* layout dim — see note below */
  padding: var(--sp-2) var(--sp-3);  /* 8 / 12 */
  border-radius: 14px;
  font-family: var(--font-body);
  font-size: 15px;                   /* matches .text-body-sm */
  line-height: 1.4;
  color: var(--text-high);
  word-break: break-word;
}

/* Inbound: left, raised surface */
.msg-bubble.inbound {
  align-self: flex-start;
  background: var(--surface-raised);
  border: 1px solid var(--surface-separator);
  border-bottom-left-radius: 4px;    /* "tail" corner */
}

/* Outbound: right, cyan-tinted */
.msg-bubble.outbound {
  align-self: flex-end;
  background: var(--accent-surface);          /* rgba(92,212,232,0.08) */
  border: 1px solid rgba(92,212,232,0.20);    /* derived from --accent-primary; not a new brand colour */
  border-bottom-right-radius: 4px;
}

/* Sending: optimistic, dimmed until ack */
.msg-bubble.sending { opacity: 0.6; }

/* Failed: red edge + tappable retry */
.msg-bubble.failed {
  border-color: var(--signal-red);
  border-left: 3px solid var(--signal-red);
  cursor: pointer;
  min-height: var(--touch-min);     /* whole bubble is a 48px retry target */
}

.msg-meta {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 4px;
  font-family: var(--font-mono);
  font-size: 11px;
  color: var(--text-low);
}

.msg-meta .msg-status-sent  { color: var(--text-low); }    /* done glyph */
.msg-meta .msg-status-fail  { color: var(--signal-red); }  /* error_outline glyph + "Failed to send" */

/* Day separator between messages on different dates */
.msg-day-sep {
  align-self: center;
  margin: var(--sp-2) 0;
  font-family: var(--font-mono);
  font-size: 11px;
  letter-spacing: 0.08em;
  text-transform: uppercase;
  color: var(--text-low);
}
```

Animation: new bubbles animate in with the existing `.list-item-add` class (design-system.css §17, `listItemAdd` keyframes) or `slideInUp` for the inbound-while-open case. Reduced-motion is already handled globally (§26).

**The one non-token value:** `max-width: 72%` and the `14px`/`4px` bubble radii are **layout dimensions**, not brand tokens — they have no `:root` equivalent (the design system tokenizes spacing on a 4px scale via `--sp-*` but does not tokenize component max-widths or border-radii; existing components like `.phone-card` hardcode `border-radius: 8px` inline, so hardcoding bubble radii here is consistent with house style). No owner sign-off needed; flagged only for completeness.

---

## §Unread dot

The leading unread indicator on voicemail/text rows. Reuse the dot already defined for `.phone-pill::before` (design-system.css line 5275) or a bare span:

```css
.unread-dot {
  width: 8px; height: 8px;
  border-radius: 50%;
  background: var(--accent-primary);
  box-shadow: 0 0 6px var(--accent-glow);   /* existing token */
  flex-shrink: 0;
}
```

## §Rail-tab badge

The unread count on the Voicemail/Texts rail tabs and the topbar Phone pill reuses `.nav-badge` **verbatim** (design-system.css §7, lines 503–519) — amber pill, `--font-mono`, `--text-inverse`. No new CSS. Mount it inside the `.phone-rail-tab` (absolute top-right) and inside the topbar `.nav-pill` exactly as the Queue pill does today (`MainLayout.razor`).

## §Reconnecting banner

```css
.gv-reconnect-banner {
  display: flex; align-items: center; gap: var(--sp-2);
  padding: var(--sp-2) var(--sp-4);
  background: rgba(240,168,48,0.10);        /* derived from --signal-amber, matches .phone-pill.amber bg */
  border-bottom: 1px solid var(--surface-separator);
  color: var(--signal-amber);
  font-family: var(--font-body);
  font-size: 13px;
}
```

All three above use only existing tokens / existing-token-derived alphas that already appear in `.phone-pill` definitions. Nothing here needs a new `:root` entry.
