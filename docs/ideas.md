# Feature ideas

Candidates for future work. They're ranked by value for everyday system-wide listening, with a rough
effort estimate against the current code.

1. **Undo / redo + A/B compare**: high value, low effort. Push a cloned `EqState` when a drag or knob edit ends; A/B = two slots plus a "copy" action.
2. **Auto gain**: high value, low effort. Estimate make-up gain from the summed EQ curve, weighted toward the mids, without measuring levels. This stops "louder sounds better" bias while tweaking.
3. **Gain scale (global %)**: medium value, low effort. One slider that scales the gain of every bell and shelf ("same curve, less of it"). Apply it when building `DspSnapshot`.
4. **Musical-note entry**: low effort. Accept "A4", "C#2+13" (cents) in the frequency field. Only `Knob.ParseLoose` needs to change.
5. **Analog-matched filter design (de-cramping)**: high value, medium effort. The RBJ/bilinear biquads squash bells and shelves near Nyquist (very visible above ~10 kHz at 44.1/48 kHz). A matched second-order design fixes this with zero latency: see M. Vicanek, "Matched Second Order Digital Filters" (2016), or Orfanidis' prescribed-Nyquist-gain EQ. It would only change `FilterDesign`, and the self-test already checks the response shapes.
6. **Band solo**: medium effort. Listen only to what a band touches. Bell or shelf: a band-pass at its freq/Q. Cut: play what is being removed. Hold a button to solo; drag sideways to move the frequency.
7. **Spectrum grab**: medium effort. Hover over the analyzer for a moment, the spectrum freezes and peaks get frequency labels, then drag a peak to create a Bell with an automatic Q.
8. **Horizontal zoom**: medium effort. Drag the frequency scale up/down to zoom around that point, left/right to pan, double-click to reset.
9. **Analyzer options**: low effort. Range 60/90/120 dB, resolution 1024–8192 points (fixed at 4096 now), release speed, and Freeze (max-hold).
10. **Gain-Q interaction**: low effort. Optional analog-style behaviour for Bells: Q narrows as gain rises, and very narrow Q adds a little gain.
11. **Multi-select**: medium effort. Ctrl/Shift-click or rubber-band selection; dragging moves all selected bands and scales their gains relative to each other.
12. **Per-band L/R placement + split**: medium effort. Process left or right only, and split a band into an L/R or M/S pair. Changes the model (placement enum instead of `MidSide` + `MS`) and the ini band format.
13. **Brickwall cuts, flat tilt, slope for every shape**: medium effort. Brickwall really needs linear phase to be useful.
14. **Dynamic EQ**: high effort. Each Bell or Shelf gets a dynamic range (±30 dB) and an auto or manual threshold, triggered by a band-limited copy of the input. It could tame harsh peaks or sibilance only when they occur.
15. **Linear phase**: skip. It adds 70 ms to 1.5 s of latency, which is a poor trade for live system audio.

## Shortcut ideas

- **Alt+click currently deletes a band**, and there's no undo yet, so a slip loses the band. Consider Alt+click = bypass, keeping Del and the ✕ button for delete.
- Ctrl+Alt+click a node to cycle its shape; Alt+Shift+click to cycle its slope.
- Alt+drag to constrain movement to one axis; Ctrl+drag vertically to change Q.
- Double-click a knob to type a value and Ctrl+click to reset (today: right-click to type, double-click to reset).
