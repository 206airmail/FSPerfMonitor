# Review and validation

These are static design artifacts. Open them as local files. The mockups simulate permissions and settings; they do not modify Windows, connect to the game or publish the home-network dashboard.

## Functional evidence

`node design/verify.mjs` uses the installed Chrome in a temporary profile inside `design/qa/`. The profile is removed after the run. No package installation or external dependency is needed. This machine's restricted child-process environment required `--in-process-gpu` and `--no-sandbox` for the isolated headless renderer; these are test-runner flags, not app settings or instructions for a user's browser. It visits only local mockups and blocks HTTP/HTTPS page requests.

The final run passed 58 render cases, recorded in [qa/checks.json](qa/checks.json). It covers normal pages at 1280×900, 1024×900, 390×844, and a further constrained 320px width; requested dashboard states; phone, ready, sign-out, cancellation and failed setup; log pause, older-lines, reconnect, empty, restart and gap states; proposed light theme on each page. No horizontal page overflow, JavaScript exceptions or external requests were observed.

Interaction checks: phone inputs are read-only with hidden mutation controls; setup shows sign-out-needed; invalid port has associated error text; log text filtering ignores case; selected levels filter lines while preserving markers; Clear empties this view; Jump returns to live; first-run setup becomes sign-out-needed; manual light selection changes the surface; native Advanced disclosure opens by keyboard.

Snapshot formatting is source-reviewed against the records: all fields have destinations in UI_SPEC, including both latency values, p50/p99, plugin services, reason, arbitrary fields and per-module sample counts. The supplied waiting capture remains byte-identical. Unsupported numeric values are em dashes; detach suppresses zeros and preserves explicitly dated saved plugin status.

## Rendered visual review

Reviewed screenshots of the dashboard on desktop and 390px phone, expanded CPU Advanced on phone, PC and phone settings, welcome, tray icons, log, gap marker, and proposed light dashboard. Also reviewed the three original skill images and wide/narrow DFTracker peer-card references.

The focal order is verdict → featured FPS → peer metrics → trend. Near-white titles and one lime metric give the monitoring task emphasis without competing with the studio/product lockup. The restrained header shading establishes identity; analytical regions stay flat. Wide metrics align with equal column widths and equivalent label/value tiers. Phone metric rows retain the same hierarchy with large numerals and wrapping explanations. Advanced stays dense and neutral, with complete wrapping fields instead of a wide grid that requires sideways page scrolling.

The initial phone chart scaled its 940-unit desktop drawing into a narrow SVG, making ticks too small and leaving unnecessary empty space. Corrected it to draw at actual content width and redrew on resize/disclosure; re-rendered and inspected the phone chart. Axis labels are now readable at the intended 12px. Added light-Windows tray variants and a dark backing to the app mark so the icon remains distinguishable on the proposed light header. Checked real 16/32px icon footprints in the tray preview; no tiny CPU/GPU badge is used.

Phone settings preserve readable On/Off states and copyable folder/port instead of dimming the whole form. Long paths scroll inside their input; they do not widen the document. Log lines wrap without losing content and have textual levels as well as semantic borders. The gap marker remains an explicit sentence. The local preview controls consume some log height; they are omitted in production.

## Limits

No physical phone, Windows tray rasterization, assistive-technology session, elevated Windows setup or live snapshot/log transport was exercised. OS font/scaling and browser combinations beyond this local Chrome remain integration checks. Native keyboard behaviors were retained, but a full screen-reader audit and actual 200% OS scaling were not performed. 320px reflow is a constrained-layout check, not a claim of physical-device coverage. Permission-request progress and host-backed save/failed-save results need the future settings API; static demo success must never be mistaken for a host result. Light colors and the official studio-mark choice remain review items in UI_SPEC.
