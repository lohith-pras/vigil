# Vigil design

The durable visual rules for the panel. Mockups: the "Vigil panel redesign" canvas. Implementation: `src/Vigil/PanelWindow.xaml`.

## Idea

A tray instrument, not a dashboard: readings set like a meter's display, separated by hairlines, on pure black. No cards inside cards, no uppercase eyebrows, no gradients.

## Material

- **Shell:** 1 px ring `#1FFFFFFF`, 4 px of black, then the panel, opaque `#000000`.
- **No acrylic.** An 80 % panel over acrylic turned grey on bright wallpapers and with transparency effects off. Opaque black looks the same on every desktop and costs no compositor work.
- **Hairlines** `#12FFFFFF` separate sections. Hover wash `#0AFFFFFF`.
- **Hatch** (−45°, 1 px, every 5 px, `#24FFFFFF`) fills the unused part of every bar: headroom, not a grey track.
- **Dot field** for history: 60 columns × 6 dots, unused dots `#10FFFFFF`, newest column cobalt.

## Type

One family for words, one for numbers. Embedded (SIL OFL) from `src/Vigil/Fonts`.

| Use | Face | Size |
|---|---|---|
| Wordmark `vigil` (lowercase, i-dot is the cobalt dot) | Geist SemiBold | 21 |
| Big reading (countdown) | Geist Mono Medium | 40 |
| Readings (CPU %, memory %) | Geist Mono Medium | 22 / 20 |
| Row titles | Geist Medium | 15 |
| Body, options | Geist | 14 / 13 |
| Section headings (sentence case) | Geist Medium, `#A1A1AA` | 13 |
| Help, labels | Geist, `#8B8B94` | 12 |
| Axis, units, small data | Geist Mono, `#8B8B94` | 11 |

Every number is Geist Mono, so values never shift width as they change.

## Colour (restrained)

| Token | Value | Only for |
|---|---|---|
| Text | `#FAFAFA` | primary text, bars at rest |
| SubText | `#A1A1AA` | headings, secondary values |
| Faint | `#8B8B94` | help, labels, axis |
| Accent (cobalt) | `#4C7DFF` | the active tab dot, a switch that's on, the newest reading, the wordmark dot. Nothing else. |
| Good | `#3DD68C` | awake / charging |
| Warn | `#F5B54A` | high (memory ≥ 80 %) |
| Danger | `#F2555A` | low battery, End?, drives > 85 % |

Selected chips are inverted (white fill, black text), not cobalt.

## Controls

- **Tabs:** icon only, no fill. Inactive icons `#6B6B74`, the active one full white with the sliding cobalt dot below.
- **Durations:** one 4 × 2 grid in a 10 px hairline frame with `#14FFFFFF` dividers. Cells are borderless; hover and the white selected fill sit 3 px inside the cell (radius 6). Durations in Geist Mono. Other chips (Until Start, apps) stay 34 px pills.
- **Keep awake off:** the big reading shows `--:--` in Faint, with "Off · normal power settings" beside it.
- **Switch:** 38 × 22, knob slides, track turns cobalt when on.
- **Segmented control** (sort): two half-pills inside a hairline ring.
- **Row action** (End): 24 px mini pill; armed = red fill, "End?".
- **Footer:** a hairline, then Settings (left) and Quit (right) as quiet SubText actions; hover adds a wash and white text.
- **Header status:** the ram figure turns amber at 80 %, same rule as the System tab.

## Motion

Slow and quiet: one curve for everything, `(.45, .05, .25, 1)` (`Motion.Soft`). Nothing overshoots, nothing travels more than a few px, and nothing moves unless a value changed. All of it runs only while the panel is visible. Code: `Motion.cs`, `RollingText.cs`.

| What | How |
|---|---|
| Panel open | 5 px rise + fade, 400 ms; sections of the page rise 4 px + fade, 700 ms, 70 ms apart |
| Panel close | instant |
| Tab change | dot travels 550 ms and stretches to 2.4x mid-way; page fades in, 5 px from the side it came from, 500 ms |
| Wordmark eye | the cobalt dot blinks every 10 s (every 6 s while keep-awake holds) |
| Awake dot (header) | soft pulse every 4 s |
| Readings | only the digits that changed slide up into place, 500 ms |
| Countdown | rolling digits; the colon eases to 45 % and back every 2 s |
| Dot field | slides one column per sample, 950 ms; the new column lights bottom-up, 600 ms per dot, 55 ms apart |
| Bars | ease to the new value over 1.4 s; memory turns amber over 800 ms |
| Chip selected | white fill spreads from the click point, 750 ms |
| Traffic | dots fall (download) / climb (upload); 3.4 s per pass down to 1.8 s at high rates; hidden when idle |
| Speed test | cobalt hairline sweeps the results while running, 2.6 s per pass |
| Process rows | glide to their new rank, 800 ms; an ended app fades out 6 px left, 500 ms |
| End armed | fades to red over 450 ms; a line drains along the bottom for the 3 s it stays armed |
| Press (every button, chip, tab) | scale .97, 140 ms ease-out |
| Switch | knob 200 ms in-out, track colour 180 ms |
| "Show animations in Windows" off | fades only, nothing slides or loops |
| Self-test (`Motion.Off`) | nothing animates, so screenshots are settled |
