export default function (view) {
    const pluginId = "7d6d91ed-0f36-46ea-9868-9623283b6b51";
    const sideNames = ["top", "right", "bottom", "left"];
    const sideTuningFields = sideNames.flatMap(side => ["brightness", "redGain", "greenGain", "blueGain"].map(field => `${side}${field[0].toUpperCase()}${field.slice(1)}Percent`));
    const colourTuningFields = [
        "brightnessPercent", "saturationPercent", "redGainPercent", "greenGainPercent", "blueGainPercent",
        "blackLevelFloorPercent", "wallColourCorrectionPercent", ...sideTuningFields
    ];
    const numericFields = [
        "wledHttpPort", "realtimeProtocol", "outputDelayMilliseconds", "outputFramesPerSecond", "stopFadeMilliseconds",
        "topLedCount", "rightLedCount", "bottomLedCount", "leftLedCount", "analysisFramesPerSecond", "samplingDepthPercent",
        "minimumColourHoldMilliseconds", ...colourTuningFields
    ];
    const defaults = {
        WledHttpPort: 80, RealtimeProtocol: 0, OutputDelayMilliseconds: 0, OutputFramesPerSecond: 30,
        StopFadeMilliseconds: 250, TopLedCount: 265, RightLedCount: 150,
        BottomLedCount: 266, LeftLedCount: 150, AnalysisWidth: 160, AnalysisFramesPerSecond: 30,
        SamplingDepthPercent: 10, MinimumColourHoldMilliseconds: 0, BrightnessPercent: 100, SaturationPercent: 100,
        RedGainPercent: 100, GreenGainPercent: 100, BlueGainPercent: 100,
        BlackLevelFloorPercent: 0,
        WallColourCorrectionPercent: 100,
        TopBrightnessPercent: 100, TopRedGainPercent: 100, TopGreenGainPercent: 100, TopBlueGainPercent: 100,
        RightBrightnessPercent: 100, RightRedGainPercent: 100, RightGreenGainPercent: 100, RightBlueGainPercent: 100,
        BottomBrightnessPercent: 100, BottomRedGainPercent: 100, BottomGreenGainPercent: 100, BottomBlueGainPercent: 100,
        LeftBrightnessPercent: 100, LeftRedGainPercent: 100, LeftGreenGainPercent: 100, LeftBlueGainPercent: 100
    };
    // Soft, muted, contemporary interior tones -- the kind of wall colour
    // actually behind a TV today (warm off-whites, greiges, dusty sage and
    // blue, soft clay) -- not swatches sampled evenly across the full colour
    // wheel. Saturated primaries are deliberately absent.
    const wallColourPresets = [
        { label: "White — no correction", hex: "#ffffff" },
        { label: "Warm white / cream", hex: "#f2ede1" },
        { label: "Soft greige", hex: "#cabfaf" },
        { label: "Light grey", hex: "#c7c4bd" },
        { label: "Warm taupe", hex: "#a89984" },
        { label: "Soft sage green", hex: "#a7ae98" },
        { label: "Muted olive", hex: "#7c7a5e" },
        { label: "Dusty blue", hex: "#8ea0ac" },
        { label: "Soft clay / terracotta", hex: "#c68f74" },
        { label: "Warm sand / beige", hex: "#ddc8a3" },
        { label: "Muted dusty rose", hex: "#d3bcb3" },
        { label: "Graphite / charcoal", hex: "#3c3b38" }
    ];
    const ledCountFields = ["topLedCount", "rightLedCount", "bottomLedCount", "leftLedCount"];
    let loadedConfig = null;
    let discoveredControllers = [];
    let knownDevices = [];
    const byId = id => view.querySelector(`#${id}`);
    const fieldKey = field => field[0].toUpperCase() + field.slice(1);
    const show = (id, visible) => { byId(id).style.display = visible ? "block" : "none"; };

    function setDelayLabel() {
        const value = Number(byId("outputDelayMilliseconds").value);
        byId("outputDelayValue").textContent = value === 0 ? "0 ms (no delay)" : `${value} ms`;
    }

    function setColourLabels() {
        byId("brightnessValue").textContent = `${byId("brightnessPercent").value}%`;
        const saturation = Number(byId("saturationPercent").value);
        byId("saturationValue").textContent = saturation === 100 ? "100% (faithful)" : `${saturation}%`;
        ["red", "green", "blue"].forEach(channel => {
            byId(`${channel}GainValue`).textContent = `${byId(`${channel}GainPercent`).value}%`;
        });
        const blackFloor = Number(byId("blackLevelFloorPercent").value);
        byId("blackLevelFloorValue").textContent = blackFloor === 0 ? "0% (off)" : `${blackFloor}%`;
        byId("wallColourCorrectionValue").textContent = `${byId("wallColourCorrectionPercent").value}%`;
        sideNames.forEach(side => {
            ["brightness", "redGain", "greenGain", "blueGain"].forEach(field => {
                const id = `${side}${field[0].toUpperCase()}${field.slice(1)}Percent`;
                byId(`${side}${field[0].toUpperCase()}${field.slice(1)}Value`).textContent = `${byId(id).value}%`;
            });
        });
    }

    function createSideTuningCards() {
        byId("sideTuningCards").innerHTML = sideNames.map(side => {
            const title = side[0].toUpperCase() + side.slice(1);
            const control = (field, label, min, max) => {
                const id = `${side}${field[0].toUpperCase()}${field.slice(1)}Percent`;
                const valueId = `${side}${field[0].toUpperCase()}${field.slice(1)}Value`;
                return `<div class="inputContainer" style="margin:.45em 0"><input is="emby-input" id="${id}" type="range" min="${min}" max="${max}" step="1" label="${label}" /><div class="fieldDescription">Now: <strong id="${valueId}"></strong></div></div>`;
            };
            return `<div style="border-left:3px solid currentColor;padding-left:.8em"><strong>${title}</strong>${control("brightness", "Brightness", 1, 100)}${control("redGain", "Red", 50, 150)}${control("greenGain", "Green", 50, 150)}${control("blueGain", "Blue", 50, 150)}</div>`;
        }).join("");
    }

    function switchTab(tab) {
        view.querySelectorAll(".raTab").forEach(button => {
            button.setAttribute("aria-selected", String(button.dataset.tab === tab));
        });
        view.querySelectorAll(".raTabPanel").forEach(panel => {
            panel.hidden = panel.dataset.tabPanel !== tab;
        });
        try {
            window.localStorage.setItem("realtimeAmbilight.activeTab", tab);
        } catch {
            // Private browsing or disabled storage: the tab still switches, it
            // just will not be remembered for next time.
        }
    }

    function restoreLastTab() {
        let saved = null;
        try {
            saved = window.localStorage.getItem("realtimeAmbilight.activeTab");
        } catch {
            saved = null;
        }
        const valid = [...view.querySelectorAll(".raTab")].some(button => button.dataset.tab === saved);
        switchTab(valid ? saved : "tv");
    }

    function populateWallColourPresets() {
        const select = byId("wallColourPreset");
        select.textContent = "";
        wallColourPresets.forEach(preset => select.add(new Option(preset.label, preset.hex)));
        select.add(new Option("Custom…", "custom"));
    }

    function syncWallColourPresetFromHex(hex) {
        const normalized = (hex || "#ffffff").toLowerCase();
        const match = wallColourPresets.find(preset => preset.hex === normalized);
        byId("wallColourPreset").value = match ? match.hex : "custom";
        show("wallColourCustomContainer", !match);
    }

    function addRangeScales() {
        view.querySelectorAll('input[type="range"]').forEach(range => {
            if (range.dataset.rangeScaleAttached) {
                return;
            }

            range.dataset.rangeScaleAttached = "true";
            const scale = document.createElement("div");
            scale.className = "fieldDescription";
            scale.style.cssText = "display:flex;justify-content:space-between;margin-top:-.25em;font-size:.82em";
            range.insertAdjacentElement("afterend", scale);

            const suffix = range.id.includes("Percent") ? "%" : range.id.includes("Milliseconds") ? " ms" : "";
            const update = () => {
                scale.innerHTML = `<span>${range.min}${suffix}</span><strong>${range.value}${suffix}</strong><span>${range.max}${suffix}</span>`;
            };
            range.addEventListener("input", update);
            update();
        });
    }

    // A blind tap on the same spot repeatedly -- eyes on the TV, not the phone
    // -- is much easier than dragging a thin slider precisely while looking
    // away. Scoped to the calibration section only: the sliders elsewhere on
    // this page are set once, not nudged while watching a live result.
    //
    // The buttons must sit in a flex row WITH the range, not merely before/
    // after it in source order: emby-input upgrades <input> in place but the
    // element itself is still block-level by default, so plain sibling
    // buttons stack above/below it -- exactly the mobile layout bug this was
    // written to fix. Wrapping forces a row regardless of how emby-input
    // renders internally.
    function addStepButtons(range) {
        if (range.dataset.stepButtonsAttached) {
            return;
        }

        range.dataset.stepButtonsAttached = "true";
        const nudge = direction => {
            const step = Number(range.step) || 1;
            const min = Number(range.min);
            const max = Number(range.max);
            range.value = String(Math.min(max, Math.max(min, Number(range.value) + (direction * step))));
            range.dispatchEvent(new Event("input", { bubbles: true }));
        };

        const button = (label, direction) => {
            const el = document.createElement("button");
            el.type = "button";
            el.className = "raised";
            el.textContent = label;
            el.setAttribute("aria-label", `${label === "−" ? "Decrease" : "Increase"} ${range.getAttribute("label") || "value"}`);
            el.style.cssText = "flex:0 0 auto;min-width:2.6em;padding:.3em .6em";
            el.addEventListener("click", () => nudge(direction));
            return el;
        };

        const row = document.createElement("div");
        row.style.cssText = "display:flex;align-items:center;gap:.5em;max-width:100%";
        range.replaceWith(row);
        range.style.cssText = "flex:1 1 auto;min-width:0";
        row.appendChild(button("−", -1));
        row.appendChild(range);
        row.appendChild(button("+", 1));
    }

    function addStepButtonsToSection() {
        byId("calibrationSection").querySelectorAll('input[type="range"]').forEach(addStepButtons);
    }

    // The shortest possible address, always plain http: a remote control types
    // this one arrow key at a time, and this plugin has no TLS certificate on
    // a bare local address for a TV browser's "try https first" guess to find.
    // Fixed and one-time now, too -- the TV page polls its own state instead
    // of being re-opened with new query parameters for every step.
    function updateCalibrationPatternUrl() {
        const url = `http://${window.location.host}/amb`;
        byId("calibrationPatternUrl").value = url;
        byId("openCalibrationPattern").href = url;
    }

    // Mirrors CalibrationWizard.ColourOrder server-side: 7 tuning steps (white,
    // then every RGB primary and secondary) followed by confirmation steps.
    // Only used as a fallback bound before the server's own StepCount is known.
    const wizardStepCountFallback = 12;
    const wizardTuningStepCount = 7;
    let wizardStepIndex = 0;
    let wizardPhotoIndex = 0;
    let wizardPhotoCount = 0;
    let wizardStarted = false;

    function currentTuningPayload() {
        const tuning = {};
        colourTuningFields.forEach(field => { tuning[fieldKey(field)] = Number(byId(field).value); });
        return { WallColourHex: byId("wallColourHex").value, Tuning: tuning };
    }

    // Per-step "what matters right now" controls, following ordinary display
    // calibration convention: a colour-temperature (warm/cool) control for
    // white balance rather than raw gains, one gain for a primary's own
    // strength, and a two-primary balance for a secondary -- "more red" at
    // Magenta and "more/less green" at Yellow are exactly this axis. Every
    // one of these ultimately just moves the same RedGainPercent /
    // GreenGainPercent / BlueGainPercent fields the advanced panel shows.
    const wizardStepControlSpecs = {
        White: { kind: "balance", a: "redGainPercent", b: "blueGainPercent", label: "Colour temperature", lowLabel: "Cooler", highLabel: "Warmer" },
        Red: { kind: "single", field: "redGainPercent", label: "Red intensity" },
        Green: { kind: "single", field: "greenGainPercent", label: "Green intensity" },
        Blue: { kind: "single", field: "blueGainPercent", label: "Blue intensity" },
        Yellow: { kind: "balance", a: "redGainPercent", b: "greenGainPercent", label: "Yellow balance", lowLabel: "More green", highLabel: "More red" },
        Cyan: { kind: "balance", a: "greenGainPercent", b: "blueGainPercent", label: "Cyan balance", lowLabel: "More blue", highLabel: "More green" },
        Magenta: { kind: "balance", a: "redGainPercent", b: "blueGainPercent", label: "Magenta balance", lowLabel: "More blue", highLabel: "More red" }
    };

    function renderStepControls(colourName) {
        const container = byId("wizardStepControls");
        const spec = wizardStepControlSpecs[colourName];
        if (!spec) {
            container.innerHTML = "";
            return;
        }

        if (spec.kind === "single") {
            container.innerHTML = `<div class="inputContainer"><input is="emby-input" id="wizardQuickField" type="range" min="50" max="150" step="1" label="${spec.label}" /><div class="fieldDescription">Now: <strong id="wizardQuickValue"></strong></div></div>`;
            const quick = byId("wizardQuickField");
            quick.value = byId(spec.field).value;
            const updateLabel = () => { byId("wizardQuickValue").textContent = `${quick.value}%`; };
            updateLabel();
            quick.addEventListener("input", () => {
                byId(spec.field).value = quick.value;
                updateLabel();
                retune();
            });
        } else {
            container.innerHTML = `<div class="inputContainer"><input is="emby-input" id="wizardQuickField" type="range" min="-50" max="50" step="1" label="${spec.label}" /><div class="fieldDescription">${spec.lowLabel} &harr; <strong id="wizardQuickValue"></strong> &harr; ${spec.highLabel}</div></div>`;
            const quick = byId("wizardQuickField");
            quick.value = Math.round((Number(byId(spec.a).value) - Number(byId(spec.b).value)) / 2);
            const updateLabel = () => {
                const value = Number(quick.value);
                byId("wizardQuickValue").textContent = value === 0 ? "centred" : (value > 0 ? `${spec.highLabel} (${value})` : `${spec.lowLabel} (${-value})`);
            };
            updateLabel();
            quick.addEventListener("input", () => {
                const delta = Number(quick.value);
                byId(spec.a).value = Math.min(150, Math.max(50, 100 + delta));
                byId(spec.b).value = Math.min(150, Math.max(50, 100 - delta));
                updateLabel();
                retune();
            });
        }

        addStepButtons(byId("wizardQuickField"));
    }

    function describeStatus(state) {
        const active = state.PreviewActive ?? state.previewActive;
        const tvConnected = state.TvConnected ?? state.tvConnected;
        if (!tvConnected) {
            return "Waiting for the TV to open the link above…";
        }
        return active
            ? "Live — adjust the sliders below."
            : "TV connected; waiting for it to load this step's photo…";
    }

    function renderWizardState(state) {
        const stepIndex = state.StepIndex ?? state.stepIndex ?? 0;
        const stepCount = state.StepCount ?? state.stepCount ?? wizardStepCountFallback;
        const colourName = state.ColourName ?? state.colourName ?? "";
        const isConfirmation = state.IsConfirmationStep ?? state.isConfirmationStep ?? (stepIndex >= wizardTuningStepCount);
        const isLastStep = state.IsLastStep ?? state.isLastStep ?? (stepIndex === stepCount - 1);
        const photoCount = state.PhotoCount ?? state.photoCount ?? 0;
        wizardStepIndex = stepIndex;
        wizardPhotoIndex = state.PhotoIndex ?? state.photoIndex ?? 0;
        wizardPhotoCount = photoCount;
        byId("wizardStepLabel").textContent = isConfirmation
            ? `Confirmation ${stepIndex - wizardTuningStepCount + 1} of ${stepCount - wizardTuningStepCount} — ${colourName}`
            : `${stepIndex + 1} of ${wizardTuningStepCount} — ${colourName} tuning`;
        renderStepControls(colourName);
        byId("wizardPrev").disabled = stepIndex === 0;
        byId("wizardNext").disabled = isLastStep;
        // Hidden, not merely disabled, when a step has only one photo: a
        // "Try another photo" button that can never do anything is a dead
        // affordance the operator otherwise keeps running into on almost
        // every step (only White currently has more than one photo).
        show("wizardAnotherPhoto", photoCount > 1);
        byId("finishCalibrationWizard").textContent = isLastStep ? "✓ Done — finish calibration" : "Stop & release LEDs";
        byId("calibrationPreviewStatus").textContent = describeStatus(state);
    }

    function showWizardStarted(started) {
        wizardStarted = started;
        byId("wizardNotStarted").hidden = started;
        byId("wizardActive").hidden = !started;
    }

    // Moves the wizard on the server, which the already-open TV page picks up
    // on its next poll and uploads its own sampled edges from the new step's
    // photo -- this call never needs to know a photo's actual colours itself.
    function moveWizard(stepIndex, photoIndex) {
        wizardStepIndex = Math.max(0, Math.min(wizardStepCountFallback - 1, stepIndex));
        wizardPhotoIndex = Math.max(0, photoIndex);
        return window.ApiClient.ajax({
            type: "POST",
            url: window.ApiClient.getUrl("RealtimeAmbilight/Calibration/WizardState"),
            data: JSON.stringify({
                StepIndex: wizardStepIndex,
                PhotoIndex: wizardPhotoIndex,
                ...currentTuningPayload()
            }),
            contentType: "application/json",
            dataType: "json"
        }).then(renderWizardState);
    }

    // The TV-facing surface (and this state) only exists while a calibration
    // is started -- see CalibrationWizardState.IsArmed -- so a 404 here just
    // means nothing is running right now, not an error to report.
    function loadWizardState() {
        return window.ApiClient
            .getJSON(window.ApiClient.getUrl("RealtimeAmbilight/Calibration/WizardState"))
            .then(state => { showWizardStarted(true); renderWizardState(state); })
            .catch(() => { showWizardStarted(false); });
    }

    function startWizard() {
        return window.ApiClient.ajax({
            type: "POST",
            url: window.ApiClient.getUrl("RealtimeAmbilight/Calibration/Start"),
            dataType: "json"
        }).then(state => { showWizardStarted(true); renderWizardState(state); });
    }

    // A slider's own "input" event fires on every drag tick; this coalesces a
    // burst of them into one request so the LEDs still feel live without
    // flooding the server. Retune only ever recolours whatever is already
    // showing -- it uploads nothing and never fetches a photo.
    let retuneTimer = null;
    function retune() {
        setColourLabels();
        if (!wizardStarted) {
            return;
        }
        clearTimeout(retuneTimer);
        retuneTimer = setTimeout(() => {
            window.ApiClient.ajax({
                type: "POST",
                url: window.ApiClient.getUrl("RealtimeAmbilight/Calibration/Retune"),
                data: JSON.stringify(currentTuningPayload()),
                contentType: "application/json",
                dataType: "json"
            }).then(response => {
                if (response?.Message ?? response?.message) {
                    byId("calibrationPreviewStatus").textContent = response.Message ?? response.message;
                }
            }).catch(() => {});
        }, 120);
    }

    function finishWizard() {
        clearTimeout(retuneTimer);
        return window.ApiClient.ajax({
            type: "POST",
            url: window.ApiClient.getUrl("RealtimeAmbilight/Calibration/Finish")
        }).then(() => {
            showWizardStarted(false);
        }).catch(() => {
            byId("calibrationPreviewStatus").textContent = "The stop request did not complete; the preview will release on WLED's normal timeout.";
        });
    }

    function setDepthLabel() {
        const percent = Number(byId("samplingDepthPercent").value);
        byId("samplingDepthValue").textContent = percent === 10 ? "10% (recommended)" : `${percent}%`;
        updateLayoutDiagram();
    }

    function setMinimumColourHoldLabel() {
        const value = Number(byId("minimumColourHoldMilliseconds").value);
        byId("minimumColourHoldValue").textContent = value === 0 ? "0 ms (off)" : `${value} ms`;
    }

    function updateLayoutDiagram() {
        const depth = Math.max(1, Math.min(30, Number(byId("samplingDepthPercent").value) || 10));
        byId("ledLayoutDiagram").style.setProperty("--sampling-inset", `${depth}%`);
        byId("samplingDiagramLabel").textContent = `Sampled edge: ${depth}%`;
        ["top", "right", "bottom", "left"].forEach(side => {
            const title = side[0].toUpperCase() + side.slice(1);
            byId(`${side}LedDiagramLabel`).textContent = `${title} · ${Number(byId(`${side}LedCount`).value) || 0}`;
        });
    }

    function setAnalysisLabel() {
        const width = Math.max(16, Number(byId("analysisWidth").value) || defaults.AnalysisWidth);
        byId("analysisSizeValue").textContent = `${width} × ${analysisHeight()} pixels`;
    }

    // A configuration written by hand, or by an older build, can hold a width the
    // preset list does not offer. Keep it selectable instead of silently moving
    // the user to a different resolution on the next save.
    function selectAnalysisWidth(width) {
        const select = byId("analysisWidth");
        if (![...select.options].some(option => Number(option.value) === width)) {
            select.add(new Option(`${width} × ${Math.round((width * 9) / 16)} — custom`, String(width)));
        }
        select.value = String(width);
    }

    // Never save an empty name for a bound device: the plugin falls back to the
    // name when a device id changes, so losing it silently breaks the binding.
    function targetDeviceName() {
        const select = byId("targetDeviceId");
        if (!select.value) {
            return "";
        }
        return select.selectedOptions[0]?.dataset.deviceName
            || knownDevices.find(device => device.id === select.value)?.name
            || loadedConfig?.TargetDeviceName
            || "";
    }

    function setLedTotal() {
        const total = ledCountFields.reduce((sum, field) => sum + (Number(byId(field).value) || 0), 0);
        byId("ledTotalValue").textContent = `${total} LEDs`;
        // The layout section collapses, so its summary has to carry the answer.
        byId("ledLayoutSummary").textContent = ledCountFields
            .map(field => Number(byId(field).value) || 0)
            .join(" / ") + ` — ${total} LEDs in total`;

        const selected = discoveredControllers.find(candidate => candidate.Host === byId("wledCandidates").value);
        const reported = selected?.LedCount ?? 0;
        byId("ledTotalCheck").textContent = reported <= 0 || total === 0
            ? ""
            : total === reported
                ? " — matches the selected controller."
                : ` — but the selected controller reports ${reported} LEDs, so these numbers are wrong.`;
        updateLayoutDiagram();
    }

    function analysisHeight() {
        const width = Math.max(16, Number(byId("analysisWidth").value) || defaults.AnalysisWidth);
        return Math.max(16, Math.round(width * 9 / 16));
    }

    function lastUsedLabel(value) {
        if (!value) {
            return "never used";
        }
        const used = new Date(value);
        if (Number.isNaN(used.getTime())) {
            return "never used";
        }
        const days = Math.floor((Date.now() - used.getTime()) / 86400000);
        if (days <= 0) {
            return `today at ${used.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}`;
        }
        return days === 1 ? "yesterday" : `${days} days ago`;
    }

    // Two sources are needed, and neither is sufficient alone. /Devices knows
    // devices that are switched off, which /Sessions cannot show. But a live
    // session can carry a device id that /Devices does not list at all, and it
    // is that id which playback events carry -- so binding from /Devices alone
    // can produce a binding that never matches anything.
    function mergeDevices(devices, sessions) {
        const merged = new Map();
        devices.filter(device => device.Id).forEach(device => merged.set(device.Id, {
            id: device.Id,
            name: device.CustomName || device.Name || "Unnamed device",
            app: device.AppName || "",
            lastUsed: device.DateLastActivity || "",
            connected: false
        }));
        sessions.filter(session => session.DeviceId).forEach(session => {
            const existing = merged.get(session.DeviceId);
            merged.set(session.DeviceId, {
                id: session.DeviceId,
                name: session.DeviceName || existing?.name || "Unnamed device",
                app: session.Client || existing?.app || "",
                lastUsed: session.LastActivityDate || existing?.lastUsed || "",
                connected: true
            });
        });
        return [...merged.values()].sort((left, right) =>
            new Date(right.lastUsed || 0) - new Date(left.lastUsed || 0));
    }

    function deviceLabel(device) {
        const app = device.app ? ` — ${device.app}` : "";
        const when = device.connected ? "connected now" : lastUsedLabel(device.lastUsed);
        return `${device.name}${app} (${when})`;
    }

    function populateDevices(devices, selectedDeviceId) {
        knownDevices = devices;
        const select = byId("targetDeviceId");
        select.textContent = "";
        select.add(new Option("All devices — not bound", ""));
        devices.forEach(device => {
            const option = new Option(deviceLabel(device), device.id);
            option.dataset.deviceName = device.name;
            select.add(option);
        });

        if (selectedDeviceId && !devices.some(device => device.id === selectedDeviceId)) {
            // Keep a binding to a device Jellyfin has since forgotten visible and
            // intact, instead of silently resetting it to "all devices" on save.
            const saved = new Option("Saved device (no longer known to Jellyfin)", selectedDeviceId);
            saved.dataset.deviceName = loadedConfig?.TargetDeviceName || "";
            select.add(saved);
        }

        // Do not make a new installation react to every playback by default.
        // The latest actual device is the least surprising starting point and
        // remains visible as the first concrete option after "All devices".
        select.value = selectedDeviceId || devices[0]?.id || "";
        const connected = devices.filter(device => device.connected).length;
        byId("targetDeviceStatus").textContent = devices.length === 0
            ? "Jellyfin does not know any playback devices yet. Play something on the TV once, then reload this page."
            : `${devices.length} known devices, ${connected} connected right now. Most recently used appears first.`;
    }

    function loadDevices(selectedDeviceId) {
        return Promise.all([
            window.ApiClient.getJSON(window.ApiClient.getUrl("Devices")).catch(() => ({ Items: [] })),
            window.ApiClient.getSessions().catch(() => [])
        ])
            .then(([devices, sessions]) => populateDevices(mergeDevices(devices.Items || [], sessions || []), selectedDeviceId))
            .catch(() => {
                byId("targetDeviceStatus").textContent = "The device list could not be loaded.";
                populateDevices([], selectedDeviceId);
            });
    }

    function populateCandidates(candidates) {
        const select = byId("wledCandidates");
        select.textContent = "";
        candidates.forEach(candidate => {
            const leds = candidate.LedCount > 0 ? `, ${candidate.LedCount} leds` : "";
            const version = candidate.Version ? `, WLED ${candidate.Version}` : "";
            select.add(new Option(`${candidate.Name} — ${candidate.Host}${leds}${version}`, candidate.Host));
        });

        if (candidates.length > 0) {
            const savedHost = loadedConfig?.WledHost;
            select.value = candidates.some(candidate => candidate.Host === savedHost) ? savedHost : candidates[0].Host;
            show("wledCandidatesContainer", true);
            show("manualWledContainer", false);
            byId("wledFinderStatus").textContent = candidates.length === 1
                ? "Found one WLED controller."
                : `Found ${candidates.length} WLED controllers.`;
            setLedTotal();
        } else {
            show("wledCandidatesContainer", false);
            show("manualWledContainer", true);
            byId("wledFinderStatus").textContent = "No WLED controller found. Enter the address yourself below.";
        }
    }

    function currentWledConnection() {
        const host = (byId("wledCandidatesContainer").style.display !== "none"
            ? byId("wledCandidates").value
            : byId("wledHost").value.trim()) || loadedConfig?.WledHost || "";
        if (!host) {
            return null;
        }

        const [hostName, hostPort] = host.split(":");
        // Keys here become query parameters: the controller binds "host" and
        // "port" ([FromQuery]), so the object's own property names matter.
        return { host: hostName, port: Number(hostPort) || Number(byId("wledHttpPort").value) || 80 };
    }

    // Ask the controller about the settings that silently override our output.
    function checkControllerSettings() {
        const connection = currentWledConnection();
        const abl = byId("wledAblStatus");
        if (!connection) {
            abl.textContent = "";
            return Promise.resolve();
        }

        return window.ApiClient
            .getJSON(window.ApiClient.getUrl("RealtimeAmbilight/Discovery/Settings", connection))
            .then(settings => {
                show("maxBrightnessWarning", Boolean(settings && settings.ForcesMaxBrightness));
                const maxPower = settings?.MaxPowerMilliamps ?? settings?.maxPowerMilliamps;
                abl.textContent = maxPower > 0
                    ? `Power limit (ABL) on WLED: ${maxPower} mA — read-only, this plugin never changes it.`
                    : "";
                // Detection only ever suggests turning the checkbox on; it
                // never flips it itself, so an operator's own choice (on or
                // off) always wins on every later load.
                const hasWhiteChannel = Boolean(settings && (settings.HasWhiteChannelHardware ?? settings.hasWhiteChannelHardware));
                show("rgbwSuggestion", hasWhiteChannel && !byId("sendWhiteChannel").checked);
            })
            .catch(() => {
                show("maxBrightnessWarning", false);
                show("rgbwSuggestion", false);
                abl.textContent = "";
            });
    }

    function fixForceMaxBrightness() {
        const connection = currentWledConnection();
        const status = byId("fixForceMaxBrightnessStatus");
        if (!connection) {
            return Promise.resolve();
        }

        byId("fixForceMaxBrightness").disabled = true;
        status.textContent = "Turning it off…";
        return window.ApiClient.ajax({
            type: "POST",
            url: window.ApiClient.getUrl("RealtimeAmbilight/Discovery/FixForceMaxBrightness", connection)
        }).then(() => {
            status.textContent = "Done. Checking WLED again…";
            return checkControllerSettings();
        }).then(() => {
            if (byId("maxBrightnessWarning").style.display !== "none") {
                status.textContent = "WLED still reports it as on; you may need to change it in WLED directly.";
            } else {
                status.textContent = "Fixed.";
            }
        }).catch(error => {
            status.textContent = error?.status === 403
                ? "Turn on \"Allow this plugin to fix WLED settings\" above, save, and try again."
                : (error?.responseText || "WLED did not accept the change.");
        }).finally(() => { byId("fixForceMaxBrightness").disabled = false; });
    }

    function checkControllerStatus() {
        const connection = currentWledConnection();
        const status = byId("wledLiveStatus");
        if (!connection) {
            status.textContent = "Controller status: choose or enter a WLED address.";
            return Promise.resolve();
        }

        status.textContent = "Controller status: checking…";
        return window.ApiClient
            .getJSON(window.ApiClient.getUrl("RealtimeAmbilight/Discovery/Status", connection))
            .then(result => {
                const online = result?.IsOnline ?? result?.isOnline;
                const realtime = result?.IsRealtimeActive ?? result?.isRealtimeActive;
                status.textContent = online
                    ? realtime
                        ? "● Online — realtime Ambilight is active."
                        : "● Online — WLED is ready."
                    : "● Not reachable — check address, port and network.";
            })
            .catch(() => { status.textContent = "● Not reachable — check address, port and network."; });
    }

    function findWled() {
        byId("findWled").disabled = true;
        byId("wledFinderStatus").textContent = "Scanning the local network…";
        return window.ApiClient.getJSON(window.ApiClient.getUrl("RealtimeAmbilight/Discovery/Wled"))
            .then(populateCandidates)
            .catch(() => {
                show("wledCandidatesContainer", false);
                show("manualWledContainer", true);
                byId("wledFinderStatus").textContent = "The finder is unreachable. Enter the address yourself below.";
            })
            .finally(() => { byId("findWled").disabled = false; });
    }

    function load() {
        Dashboard.showLoadingMsg();
        return window.ApiClient.getPluginConfiguration(pluginId)
            .then(config => {
                loadedConfig = config;
                byId("enabled").checked = config.Enabled !== false;
                byId("holdWhilePaused").checked = config.HoldWhilePaused !== false;
                byId("ignoreBlackBorders").checked = config.IgnoreBlackBorders !== false;
                byId("correctLedGamma").checked = config.CorrectLedGamma !== false;
                byId("autoDetectLedGamma").checked = config.AutoDetectLedGamma !== false;
                byId("allowWledControl").checked = config.AllowWledControl === true;
                byId("sendWhiteChannel").checked = config.SendWhiteChannel === true;
                byId("hueEnabled").checked = config.HueEnabled === true;
                byId("hueBrightnessPercent").value = config.HueBrightnessPercent || 100;
                byId("hueEndBehaviour").value = String(config.HueEndBehaviour ?? 0);
                if (config.HueEntertainmentConfigurationId && config.HueEntertainmentConfigurationId !== "00000000-0000-0000-0000-000000000000") {
                    show("hueSelectionSection", true);
                }
                byId("allowWledControlSummary").textContent = config.AllowWledControl === true
                    ? "This plugin may fix WLED settings for you."
                    : "This plugin only reads WLED until you turn this on.";
                numericFields.forEach(field => {
                    const key = fieldKey(field);
                    const value = field === "realtimeProtocol"
                        ? ({ Auto: 0, HyperionRawRgb: 1, Ddp: 2 }[config[key]] ?? config[key] ?? defaults[key])
                        : config[key] ?? defaults[key];
                    byId(field).value = value;
                });
                byId("wledHost").value = config.WledHost || "";
                const wallColourHex = /^#[0-9a-f]{6}$/i.test(config.WallColourHex || "")
                    ? config.WallColourHex.toLowerCase()
                    : "#ffffff";
                byId("wallColourHex").value = wallColourHex;
                syncWallColourPresetFromHex(wallColourHex);
                selectAnalysisWidth(Math.max(16, Number(config.AnalysisWidth) || defaults.AnalysisWidth));
                view.querySelectorAll('input[type="range"]').forEach(range => range.dispatchEvent(new Event("input")));
                setDelayLabel();
                setAnalysisLabel();
                setDepthLabel();
                setMinimumColourHoldLabel();
                setColourLabels();
                updateCalibrationPatternUrl();
                setLedTotal();
                return loadDevices(config.TargetDeviceId || "");
            })
            .then(findWled)
            .then(() => Promise.all([
                checkControllerSettings(),
                checkControllerStatus(),
                loadWizardState(),
                // On a normal page load (not right after an interactive
                // pairing), nothing else ever populates the entertainment-area
                // dropdown -- do it here too whenever a bridge is already
                // paired, or the saved selection has nothing to bind to.
                loadHueStatus().then(paired => paired ? refreshHueEntertainmentConfigs() : null)
            ]))
            .finally(() => Dashboard.hideLoadingMsg());
    }

    function save(event) {
        event.preventDefault();
        const usingFinder = byId("wledCandidatesContainer").style.display !== "none";
        const host = (usingFinder ? byId("wledCandidates").value : byId("wledHost").value.trim());
        if (!host) {
            Dashboard.alert("Enter a WLED address first, or pick a discovered controller.");
            return;
        }

        Dashboard.showLoadingMsg();
        const [hostName, hostPort] = host.split(":");
        const config = {
            ...loadedConfig,
            ConfigSchemaVersion: 3,
            Enabled: byId("enabled").checked,
            HoldWhilePaused: byId("holdWhilePaused").checked,
            IgnoreBlackBorders: byId("ignoreBlackBorders").checked,
            CorrectLedGamma: byId("correctLedGamma").checked,
            AutoDetectLedGamma: byId("autoDetectLedGamma").checked,
            AllowWledControl: byId("allowWledControl").checked,
            SendWhiteChannel: byId("sendWhiteChannel").checked,
            TargetDeviceId: byId("targetDeviceId").value,
            TargetDeviceName: targetDeviceName(),
            WledHost: hostName,
            AnalysisWidth: Number(byId("analysisWidth").value),
            AnalysisHeight: analysisHeight(),
            WallColourHex: byId("wallColourHex").value,
            // Hue has no field-by-field save button of its own -- pairing and
            // unlinking take effect immediately (they need the bridge itself),
            // but everything else here is a plain preference saved the same
            // way as every other tab, through this one button.
            HueEnabled: byId("hueEnabled").checked,
            HueEntertainmentConfigurationId: byId("hueEntertainmentConfig").value || "00000000-0000-0000-0000-000000000000",
            HueEntertainmentConfigurationName: byId("hueEntertainmentConfig").selectedOptions[0]?.textContent || "",
            HueBrightnessPercent: Number(byId("hueBrightnessPercent").value),
            HueEndBehaviour: Number(byId("hueEndBehaviour").value)
        };
        numericFields.forEach(field => { config[fieldKey(field)] = Number(byId(field).value); });
        if (hostPort) {
            config.WledHttpPort = Number(hostPort);
        }

        window.ApiClient.updatePluginConfiguration(pluginId, config)
            .then(result => {
                loadedConfig = config;
                return Dashboard.processPluginConfigurationUpdateResult(result);
            })
            .finally(() => Dashboard.hideLoadingMsg());
    }

    // --- Hue Entertainment (optional, off by default) ---------------------
    // A deliberately separate save path from the main form: pairing and
    // selection each take effect immediately server-side (the controller
    // saves PluginConfiguration itself). That immediacy has a sharp edge,
    // though -- the main form's own save() still spreads {...loadedConfig},
    // a snapshot taken once at page load, so afterwards clicking the main
    // Save button silently overwrites whatever pairing/selection/unlink just
    // wrote server-side (HueBridgeHost included) back to its stale, pre-Hue
    // value. Confirmed live: a bridge paired and enabled this way, followed
    // by the main Save button, left the server with HueEnabled=true but an
    // empty HueBridgeHost, which threw a UriFormatException the moment
    // playback tried to connect. refreshLoadedConfig() re-syncs the cached
    // copy after every Hue action that changes server state, so a later
    // main-form save carries the current values forward instead of
    // reverting them.
    let hueSelectedBridgeHost = "";

    function refreshLoadedConfig() {
        return window.ApiClient.getPluginConfiguration(pluginId).then(config => { loadedConfig = config; });
    }

    function describeHueState(state, issue) {
        const known = {
            Unpaired: "Not paired.",
            Ready: "Paired and ready. Starts automatically when the bound TV plays.",
            Connecting: "Connecting…",
            Streaming: "● Synchronising.",
            Paused: "● Synchronised, playback paused.",
            Recovering: "Reconnecting…",
            Stopping: "Stopping…",
            RelinkRequired: "Needs pairing again.",
        };
        const issueText = {
            TemporarilyUnreachable: " (bridge temporarily unreachable)",
            CredentialsRevoked: " (credentials were revoked on the bridge)",
            EntertainmentConfigurationMissing: " (the selected entertainment area no longer exists)",
            StreamOwnershipLost: " (another application is currently streaming to this bridge)",
        };
        return (known[state] || state) + (issueText[issue] || "");
    }

    function loadHueStatus() {
        return window.ApiClient.getJSON(window.ApiClient.getUrl("RealtimeAmbilight/Hue/Status"))
            .then(status => {
                const state = status.State ?? status.state;
                const issue = status.Issue ?? status.issue;
                const paired = status.Paired ?? status.paired;
                byId("hueStatusLine").textContent = paired
                    ? describeHueState(state, issue)
                    : "Not paired. Scan for a bridge below to get started.";
                show("hueSelectionSection", paired);
                byId("hueStartPairing").textContent = paired ? "Bridge paired" : "Pair with this bridge";
                if (paired && !hueSelectedBridgeHost) {
                    hueSelectedBridgeHost = status.BridgeHost ?? status.bridgeHost ?? "";
                }

                return paired;
            })
            .catch(() => { byId("hueStatusLine").textContent = ""; return false; });
    }

    function hueCurrentBridgeHost() {
        return (byId("hueBridgeCandidatesContainer").style.display !== "none"
            ? byId("hueBridgeCandidates").value
            : byId("hueManualBridgeHost").value.trim()) || hueSelectedBridgeHost;
    }

    function scanHueBridges() {
        byId("hueScanBridges").disabled = true;
        byId("hueFinderStatus").textContent = "Scanning the local network…";
        return window.ApiClient.getJSON(window.ApiClient.getUrl("RealtimeAmbilight/Hue/Bridges"))
            .then(candidates => {
                const select = byId("hueBridgeCandidates");
                select.textContent = "";
                candidates.forEach(candidate => {
                    const host = candidate.Host ?? candidate.host;
                    const bridgeId = candidate.BridgeId ?? candidate.bridgeId;
                    select.add(new Option(`${host} — ${bridgeId}`, host));
                });
                if (candidates.length > 0) {
                    hueSelectedBridgeHost = candidates[0].Host ?? candidates[0].host;
                    show("hueBridgeCandidatesContainer", true);
                    show("hueManualBridgeContainer", false);
                    byId("hueFinderStatus").textContent = `Found ${candidates.length} bridge(s).`;
                } else {
                    show("hueBridgeCandidatesContainer", false);
                    show("hueManualBridgeContainer", true);
                    byId("hueFinderStatus").textContent = "No bridge found. Enter its address below.";
                }
            })
            .catch(() => {
                show("hueBridgeCandidatesContainer", false);
                show("hueManualBridgeContainer", true);
                byId("hueFinderStatus").textContent = "The finder is unreachable. Enter the address yourself below.";
            })
            .finally(() => { byId("hueScanBridges").disabled = false; });
    }

    // Polls one press-link attempt every 2 s for up to 30 s, matching how
    // long an operator realistically has to walk to the bridge and press it.
    function startHuePairing() {
        const host = hueCurrentBridgeHost();
        if (!host) {
            byId("huePairingStatus").textContent = "Scan for a bridge or enter its address first.";
            return;
        }

        const button = byId("hueStartPairing");
        button.disabled = true;
        let attemptsLeft = 15;
        // The button's own label carries the primary state -- "press the
        // button now", then "bridge paired" -- since that is what is actually
        // being asked of the operator at each step; the line underneath is
        // only ever supplementary detail (a countdown, a failure reason).
        button.textContent = "Press the button on the bridge now…";
        byId("huePairingStatus").textContent = "";

        const attempt = () => window.ApiClient.ajax({
            type: "POST",
            url: window.ApiClient.getUrl("RealtimeAmbilight/Hue/Pair", { host }),
            dataType: "json",
        }).then(result => {
            const success = result.Success ?? result.success;
            if (success) {
                button.textContent = "Bridge paired";
                button.disabled = false;
                byId("huePairingStatus").textContent = "";
                return Promise.all([refreshLoadedConfig(), loadHueStatus(), refreshHueEntertainmentConfigs()]);
            }

            attemptsLeft--;
            const reason = result.FailureReason ?? result.failureReason ?? "";
            if (attemptsLeft <= 0) {
                button.textContent = "Pair with this bridge";
                byId("huePairingStatus").textContent = `Gave up: ${reason || "the bridge did not respond in time"}.`;
                button.disabled = false;
                return null;
            }

            byId("huePairingStatus").textContent = reason || "Waiting for the link button…";
            return new Promise(resolve => setTimeout(resolve, 2000)).then(attempt);
        }).catch(() => {
            button.textContent = "Pair with this bridge";
            byId("huePairingStatus").textContent = "The bridge could not be reached.";
            button.disabled = false;
        });

        return attempt();
    }

    // Populates the dropdown from the bridge's own areas, then restores
    // whichever selection is currently saved -- either the DOM's own prior
    // value (right after a fresh pairing/manual refresh) or, on a normal page
    // load where a bridge was already paired earlier, the id saved in
    // PluginConfiguration. Without the latter, a plain page load always
    // rendered this dropdown empty (nothing calls this except pairing), so
    // clicking Save without ever touching the dropdown would submit an empty
    // selection and silently clear an already-configured entertainment area.
    function refreshHueEntertainmentConfigs() {
        return window.ApiClient.getJSON(window.ApiClient.getUrl("RealtimeAmbilight/Hue/EntertainmentConfigurations"))
            .then(configs => {
                const select = byId("hueEntertainmentConfig");
                const previousValue = select.value || loadedConfig?.HueEntertainmentConfigurationId;
                select.textContent = "";
                configs.forEach(config => {
                    const id = config.Id ?? config.id;
                    const name = config.Name ?? config.name;
                    const channelCount = (config.Channels ?? config.channels ?? []).length;
                    select.add(new Option(`${name} (${channelCount} channel${channelCount === 1 ? "" : "s"})`, id));
                });
                if (previousValue) {
                    select.value = previousValue;
                }

                show("hueSelectionSection", true);
            })
            .catch(() => {});
    }

    function unlinkHue() {
        if (!window.confirm("Forget the stored Hue credentials? This does not change anything on the bridge itself.")) {
            return Promise.resolve();
        }

        return window.ApiClient.ajax({
            type: "POST",
            url: window.ApiClient.getUrl("RealtimeAmbilight/Hue/Unlink"),
        }).then(() => {
            show("hueSelectionSection", false);
            return Promise.all([refreshLoadedConfig(), loadHueStatus()]);
        });
    }

    function setHueBrightnessLabel() {
        byId("hueBrightnessValue").textContent = `${byId("hueBrightnessPercent").value}%`;
    }

    populateWallColourPresets();
    createSideTuningCards();
    addRangeScales();
    addStepButtonsToSection();
    restoreLastTab();
    view.querySelectorAll(".raTab").forEach(button => button.addEventListener("click", () => switchTab(button.dataset.tab)));
    view.addEventListener("viewshow", load);
    byId("realtimeAmbilightConfigurationForm").addEventListener("submit", save);
    byId("outputDelayMilliseconds").addEventListener("input", setDelayLabel);
    byId("samplingDepthPercent").addEventListener("input", setDepthLabel);
    byId("minimumColourHoldMilliseconds").addEventListener("input", setMinimumColourHoldLabel);
    colourTuningFields.forEach(field => byId(field).addEventListener("input", retune));
    byId("wallColourHex").addEventListener("input", retune);
    byId("analysisWidth").addEventListener("change", setAnalysisLabel);
    ledCountFields.forEach(field => byId(field).addEventListener("input", setLedTotal));
    byId("wledCandidates").addEventListener("change", () => {
        setLedTotal();
        checkControllerSettings();
        checkControllerStatus();
    });
    byId("findWled").addEventListener("click", findWled);
    byId("refreshDevices").addEventListener("click", () => loadDevices(byId("targetDeviceId").value));
    byId("refreshWledStatus").addEventListener("click", () => {
        checkControllerSettings();
        checkControllerStatus();
    });
    byId("wledHost").addEventListener("change", checkControllerStatus);
    byId("wledHttpPort").addEventListener("change", checkControllerStatus);
    byId("startCalibrationWizard").addEventListener("click", startWizard);
    byId("wizardPrev").addEventListener("click", () => moveWizard(wizardStepIndex - 1, 0));
    byId("wizardNext").addEventListener("click", () => moveWizard(wizardStepIndex + 1, 0));
    byId("wizardAnotherPhoto").addEventListener("click", () => moveWizard(wizardStepIndex, wizardPhotoIndex + 1));
    byId("copyCalibrationUrl").addEventListener("click", () => {
        const url = byId("calibrationPatternUrl").value;
        if (navigator.clipboard?.writeText) {
            navigator.clipboard.writeText(url)
                .then(() => { byId("calibrationPreviewStatus").textContent = "Link copied. Open it full-screen in the TV browser."; })
                .catch(() => { byId("calibrationPatternUrl").select(); });
        } else {
            byId("calibrationPatternUrl").select();
        }
    });
    byId("finishCalibrationWizard").addEventListener("click", finishWizard);
    byId("wallColourPreset").addEventListener("change", () => {
        const value = byId("wallColourPreset").value;
        if (value === "custom") {
            show("wallColourCustomContainer", true);
        } else {
            show("wallColourCustomContainer", false);
            byId("wallColourHex").value = value;
        }
        retune();
    });
    byId("allowWledControl").addEventListener("change", () => {
        byId("allowWledControlSummary").textContent = byId("allowWledControl").checked
            ? "This plugin may fix WLED settings for you. Save to keep it that way."
            : "This plugin only reads WLED until you turn this on.";
    });
    byId("fixForceMaxBrightness").addEventListener("click", fixForceMaxBrightness);
    byId("hueScanBridges").addEventListener("click", scanHueBridges);
    byId("hueStartPairing").addEventListener("click", startHuePairing);
    byId("hueRefreshConfigs").addEventListener("click", refreshHueEntertainmentConfigs);
    byId("hueUnlink").addEventListener("click", unlinkHue);
    byId("hueBrightnessPercent").addEventListener("input", setHueBrightnessLabel);
}
