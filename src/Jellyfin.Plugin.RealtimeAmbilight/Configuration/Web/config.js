export default function (view) {
    const pluginId = "7d6d91ed-0f36-46ea-9868-9623283b6b51";
    const sideNames = ["top", "right", "bottom", "left"];
    const sideTuningFields = sideNames.flatMap(side => ["brightness", "redGain", "greenGain", "blueGain"].map(field => `${side}${field[0].toUpperCase()}${field.slice(1)}Percent`));
    const colourTuningFields = [
        "brightnessPercent", "saturationPercent", "redGainPercent", "greenGainPercent", "blueGainPercent",
        "blackLevelFloorPercent", "wallColourCorrectionPercent", ...sideTuningFields
    ];
    // The colour-tuning wizard's six primary/secondary anchors (hue shift +
    // brightness + intensity each) that build HueCorrectionCurve server-side.
    // Deliberately not wired through byId()/colourTuningFields like the flat
    // gains above: there is no matching Advanced-tab raw slider for these --
    // only the wizard's own three-slider-per-colour controls set them -- so
    // they live in their own small state object instead of needing 18 hidden
    // DOM inputs just to have somewhere to read a .value from.
    const hueAnchorColours = ["Red", "Green", "Blue", "Yellow", "Cyan", "Magenta"];
    const hueAnchorFieldsFor = colour => [`${colour}HueShiftDegrees`, `${colour}BrightnessPercent`, `${colour}IntensityPercent`];
    const hueAnchorFields = hueAnchorColours.flatMap(hueAnchorFieldsFor);
    let hueAnchors = {};
    function resetHueAnchors(source) {
        hueAnchors = {};
        hueAnchorColours.forEach(colour => {
            hueAnchors[`${colour}HueShiftDegrees`] = Number(source?.[`${colour}HueShiftDegrees`] ?? 0);
            hueAnchors[`${colour}BrightnessPercent`] = Number(source?.[`${colour}BrightnessPercent`] ?? 100);
            hueAnchors[`${colour}IntensityPercent`] = Number(source?.[`${colour}IntensityPercent`] ?? 100);
        });
    }
    resetHueAnchors(null);

    // Direction each colour's hue-shift slider actually rotates the wheel:
    // +1 means "positive slider value = toward the next (higher-hue)
    // neighbour", -1 means "toward the previous (lower-hue) neighbour".
    // Red/Blue/Yellow/Cyan's second-named descriptor sits at the higher-hue
    // neighbour; Green's and Magenta's sit at the lower-hue one instead --
    // this is what makes each pair of labels below correct instead of
    // swapped, deliberately not the same sign for every colour.
    const hueStepSpecs = {
        Red: { lowLabel: "Pinker", highLabel: "Oranger", sign: 1 },
        Green: { lowLabel: "Bluer", highLabel: "Yellower", sign: -1 },
        Blue: { lowLabel: "Greener", highLabel: "Pinker", sign: 1 },
        Yellow: { lowLabel: "Oranger", highLabel: "Greener", sign: 1 },
        Cyan: { lowLabel: "Greener", highLabel: "Bluer", sign: 1 },
        Magenta: { lowLabel: "Redder", highLabel: "Bluer", sign: -1 }
    };

    // Which two anchors each real two-colour finetuning photo actually shows
    // and should offer sliders for -- mirrors CalibrationWizard.FinetuningColourPairs.
    const finetuningColourPairs = {
        "Blue-Green": ["Blue", "Green"],
        "Orange-Red": ["Red", "Yellow"],
        "Purple-Teal": ["Blue", "Cyan"],
        "Yellow-Pink": ["Yellow", "Magenta"]
    };
    const numericFields = [
        "wledHttpPort", "realtimeProtocol", "outputDelayMilliseconds", "outputFramesPerSecond", "stopFadeMilliseconds",
        "topLedCount", "rightLedCount", "bottomLedCount", "leftLedCount", "analysisFramesPerSecond", "samplingDepthPercent",
        "minimumColourHoldMilliseconds", "wledSmoothingMilliseconds", ...colourTuningFields
    ];
    const defaults = {
        WledHttpPort: 80, RealtimeProtocol: 0, OutputDelayMilliseconds: 0, OutputFramesPerSecond: 30,
        StopFadeMilliseconds: 250, TopLedCount: 265, RightLedCount: 150,
        BottomLedCount: 266, LeftLedCount: 150, AnalysisWidth: 160, AnalysisFramesPerSecond: 30,
        SamplingDepthPercent: 10, MinimumColourHoldMilliseconds: 0, WledSmoothingMilliseconds: 0, BrightnessPercent: 100, SaturationPercent: 100,
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
    // Latest live signals, refreshed by their own periodic polls, read by
    // the pill/summary rendering rather than each fetching their own copy.
    let latestPerformance = null;
    let latestWledStatus = null;
    let latestHueStatus = null;
    const byId = id => view.querySelector(`#${id}`);
    const fieldKey = field => field[0].toUpperCase() + field.slice(1);
    const show = (id, visible) => { byId(id).style.display = visible ? "block" : "none"; };

    function setDelayLabel() {
        const value = Number(byId("outputDelayMilliseconds").value);
        byId("outputDelayValue").textContent = value === 0 ? "0 ms (no delay)" : `${value} ms`;
    }

    function setColourLabels() {
        byId("brightnessValue").textContent = `${byId("brightnessPercent").value}%`;
        byId("whiteChannelStrengthValue").textContent = `${byId("whiteChannelStrengthPercent").value}%`;
        show("whiteChannelStrengthContainer", byId("sendWhiteChannel").checked);
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
            return `<div style="border-left:3px solid currentColor;padding-left:.8em"><strong>${title}</strong>${control("brightness", "Brightness", 1, 200)}${control("redGain", "Red", 50, 150)}${control("greenGain", "Green", 50, 150)}${control("blueGain", "Blue", 50, 150)}</div>`;
        }).join("");
    }

    // Card overview is the landing page; opening a card reveals that one
    // section (a "detail view"), each showing a read-only summary of what
    // is already configured by default, with an "Edit" button that reveals
    // the actual controls -- already pre-filled with the current values,
    // since nothing in the edit markup is ever cleared or rebuilt when
    // toggled, only shown or hidden. A section with nothing configured yet
    // opens straight into Edit instead (see each summarise* function).
    const sectionSummarisers = {};

    // The overview cards are always on screen, above whichever detail panel
    // (if any) is open below them -- they are never hidden by opening a
    // section, only the reverse.
    function switchTab(tab) {
        view.querySelectorAll(".raTabPanel").forEach(panel => {
            panel.hidden = panel.dataset.tabPanel !== tab;
        });
        view.querySelectorAll(".raCard").forEach(card => {
            card.classList.toggle("raCardActive", card.dataset.openTab === tab);
        });
        sectionSummarisers[tab]?.();
        view.querySelector(`[data-tab-panel="${tab}"]`)?.scrollIntoView({ behavior: "instant", block: "start" });
    }

    function showOverview() {
        view.querySelectorAll(".raTabPanel").forEach(panel => { panel.hidden = true; });
        view.querySelectorAll(".raCard").forEach(card => card.classList.remove("raCardActive"));
        window.scrollTo({ top: 0, behavior: "instant" });
    }

    function showSectionSummary(section) {
        show(`${section}Summary`, true);
        show(`${section}Edit`, false);
    }

    function showSectionEdit(section) {
        show(`${section}Summary`, false);
        show(`${section}Edit`, true);
    }

    function isAmbilightCalibrated() {
        return hueAnchorColours.some(colour => hueAnchors[`${colour}HueShiftDegrees`] !== 0)
            || Number(byId("brightnessPercent").value) !== 100;
    }

    // "Off" = disabled/unconfigured. "Ready" = configured and idle -- today's
    // outline-only look. "Streaming" = actually sending frames right now, a
    // deliberately different (filled) look: "On" was reported as misleading
    // because it read as "currently streaming" when it only ever meant
    // "enabled", so the two are now visually distinct states, not one pill
    // with two different real meanings.
    function setPill(id, state) {
        const el = byId(id);
        if (!el) {
            return;
        }

        el.textContent = state === "streaming" ? "Streaming" : state === "ready" ? "Ready" : state === "warn" ? "Check" : "Off";
        el.className = `raPill ra${state[0].toUpperCase()}${state.slice(1)}`;
    }

    function isWledStreaming() {
        const fps = latestPerformance?.WledRenderFps ?? latestPerformance?.wledRenderFps;
        return fps !== null && fps !== undefined;
    }

    function isHueStreaming() {
        const state = latestHueStatus?.State ?? latestHueStatus?.state;
        return state === "Streaming";
    }

    sectionSummarisers.tv = function summariseTv() {
        byId("tvSummaryDevice").textContent = deviceLabelWithIp();
        byId("tvSummaryEnabled").textContent = byId("enabled").checked ? "On" : "Off";
        byId("tvSummarySampling").textContent = `${byId("analysisWidth").value}×${analysisHeight()} @ ${byId("analysisFramesPerSecond").value} fps`;
        byId("targetDeviceId").value ? showSectionSummary("tv") : showSectionEdit("tv");
    };

    sectionSummarisers.wled = function summariseWled() {
        const host = byId("wledHost").value.trim() || loadedConfig?.WledHost || "";
        const wledName = latestWledStatus?.Name ?? latestWledStatus?.name;
        byId("wledSummaryHost").textContent = host ? (wledName ? `${wledName} (${host})` : host) : "Not set up yet";
        const counts = ledCountFields.map(field => Number(byId(field).value) || 0);
        byId("wledSummaryLeds").textContent = `${counts.reduce((a, b) => a + b, 0)} (${counts.join("/")})`;
        byId("wledSummaryRgbw").textContent = byId("sendWhiteChannel").checked
            ? `On, strength ${byId("whiteChannelStrengthPercent").value}%`
            : "Off";
        const delay = Number(byId("outputDelayMilliseconds").value) || 0;
        byId("wledSummaryTiming").textContent = `${byId("outputFramesPerSecond").value} fps, ${delay === 0 ? "no delay" : `${delay} ms delay`}`;
        host ? showSectionSummary("wled") : showSectionEdit("wled");
        refreshFpsChain();
    };

    // One row per anchor with a real deviation (hue shift, or brightness/
    // intensity pulled down from 100%) -- omits an anchor sitting exactly at
    // its default, so this list is only ever the colours actually touched,
    // not all six every time.
    function renderColourDeviation() {
        // Clamped to match PerimeterColourTuning.Anchor()'s own server-side
        // range (hue +-21 deg, brightness/intensity 50-100%) -- a value
        // saved outside that range by an older build would otherwise show a
        // number here that is not actually what gets applied.
        const rows = hueAnchorColours
            .map(colour => ({
                colour,
                hue: Math.max(-21, Math.min(21, hueAnchors[`${colour}HueShiftDegrees`] || 0)),
                brightness: Math.max(50, Math.min(100, hueAnchors[`${colour}BrightnessPercent`] ?? 100)),
                intensity: Math.max(50, Math.min(100, hueAnchors[`${colour}IntensityPercent`] ?? 100))
            }))
            .filter(a => a.hue !== 0 || a.brightness !== 100 || a.intensity !== 100)
            .map(a => {
                const parts = [];
                if (a.hue !== 0) {
                    parts.push(`${a.hue > 0 ? "+" : ""}${a.hue}°`);
                }
                if (a.brightness !== 100) {
                    parts.push(`${a.brightness}% bright`);
                }
                if (a.intensity !== 100) {
                    parts.push(`${a.intensity}% intensity`);
                }
                return `<div>${a.colour}: ${parts.join(", ")}</div>`;
            });
        byId("ambilightSummaryDeviation").innerHTML = rows.length ? rows.join("") : "None (default curve)";
    }

    sectionSummarisers.ambilight = function summariseAmbilight() {
        const calibrated = isAmbilightCalibrated();
        setPill("ambilightSummaryCalibrated", calibrated ? "ready" : "off");
        byId("ambilightSummaryCalibrated").textContent = calibrated ? "Calibrated" : "Default";
        byId("ambilightSummaryBrightness").textContent = `${byId("brightnessPercent").value}%`;
        byId("ambilightSummarySaturation").textContent = `${byId("saturationPercent").value}%`;
        const smoothingMs = Number(byId("wledSmoothingMilliseconds").value) || 0;
        byId("ambilightSummarySmoothing").textContent = smoothingMs === 0 ? "Off (fully reactive)" : `${smoothingMs} ms`;
        renderColourDeviation();
        calibrated ? showSectionSummary("ambilight") : showSectionEdit("ambilight");
    };

    sectionSummarisers.hue = function summariseHue() {
        const bridgeHost = loadedConfig?.HueBridgeHost || "";
        byId("hueSummaryBridge").textContent = bridgeHost ? `Hue Bridge (${bridgeHost})` : "Not paired yet";
        byId("hueSummaryArea").textContent = byId("hueEntertainmentConfig").selectedOptions[0]?.textContent
            || loadedConfig?.HueEntertainmentConfigurationName
            || "Not selected";
        byId("hueSummaryBrightness").textContent = `${byId("hueBrightnessPercent").value}%`;
        const response = byId("hueResponsePercent").value;
        byId("hueSummaryResponsivity").textContent = `${response} (0-100)`;
        bridgeHost && byId("hueEnabled").checked ? showSectionSummary("hue") : showSectionEdit("hue");
    };

    // Cheap: reads three already-maintained counters, no measuring work of
    // its own on the server. Polled at a modest interval regardless of
    // which section is open, so the overview card's own pill/meta text
    // stays current without needing its own separate refresh path.
    function refreshFpsChain() {
        return window.ApiClient.getJSON(window.ApiClient.getUrl("RealtimeAmbilight/Discovery/Performance"))
            .then(perf => {
                latestPerformance = perf;
                const analyse = perf?.AnalyseFps ?? perf?.analyseFps;
                const sample = perf?.SampleFps ?? perf?.sampleFps;
                const wled = perf?.WledRenderFps ?? perf?.wledRenderFps;
                const active = analyse !== null && analyse !== undefined;
                const format = value => value === null || value === undefined ? "" : `${Number(value).toFixed(1)} fps`;
                byId("fpsAnalyse").textContent = active ? format(analyse) : "Ready";
                byId("fpsSample").textContent = active ? format(sample) : "Ready";
                byId("fpsWled").textContent = active ? format(wled) : "Ready";
                view.querySelectorAll("#wledFpsChain .raFpsStage .raFpsValue").forEach(el => {
                    el.style.color = active ? "" : "#2fae4e";
                });
                byId("fpsHint").innerHTML = active
                    ? ""
                    : `Live only while something is playing on the bound device (${deviceLabelWithIp()}). <button type="button" class="raLink" data-open-tab="tv">Open TV settings</button>`;
                refreshOverviewCards();
            })
            .catch(() => {});
    }

    function refreshOverviewCards() {
        const enabled = byId("enabled").checked;
        const boundDevice = Boolean(byId("targetDeviceId").value);
        const streaming = isWledStreaming();
        setPill("raCardTvPill", !enabled ? "off" : streaming ? "streaming" : "ready");
        byId("raCardTvMeta").innerHTML = `<div>${deviceLabelWithIp()}</div><div>Ambilight ${enabled ? "on" : "off"}</div>`;

        const wledHost = byId("wledHost").value.trim() || loadedConfig?.WledHost || "";
        const wledName = latestWledStatus?.Name ?? latestWledStatus?.name;
        setPill("raCardWledPill", !wledHost ? "off" : streaming ? "streaming" : "ready");
        byId("raCardWledMeta").innerHTML = wledHost
            ? `<div>${wledName ? `${wledName} (${wledHost})` : wledHost}</div><div>${ledCountFields.map(f => Number(byId(f).value) || 0).reduce((a, b) => a + b, 0)} LEDs${byId("sendWhiteChannel").checked ? ", RGBW" : ""}</div>`
            : "Scan or enter a controller address";

        const calibrated = isAmbilightCalibrated();
        setPill("raCardAmbilightPill", calibrated ? "ready" : "off");
        byId("raCardAmbilightPill").textContent = calibrated ? "Calibrated" : "Default";
        byId("raCardAmbilightMeta").innerHTML = `<div>${calibrated ? "Custom colour calibration" : "Using the picture as recorded"}</div><div>Brightness ${byId("brightnessPercent").value}% · intensity ${byId("saturationPercent").value}%</div>`;

        const hueEnabled = byId("hueEnabled").checked;
        const huePaired = Boolean(loadedConfig?.HueBridgeHost);
        const hueStreaming = isHueStreaming();
        setPill("raCardHuePill", !hueEnabled || !huePaired ? "off" : hueStreaming ? "streaming" : "ready");
        byId("raCardHueMeta").innerHTML = hueEnabled && huePaired
            ? `<div>${loadedConfig?.HueEntertainmentConfigurationName || "Paired"}</div><div>Brightness ${byId("hueBrightnessPercent").value}%</div>`
            : huePaired ? "Paired, currently off" : "Not paired yet";
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
    // then every RGB primary and secondary) followed by 4 finetuning steps.
    // Only used as a fallback bound before the server's own StepCount is known.
    const wizardStepCountFallback = 11;
    const wizardTuningStepCount = 7;
    let wizardStepIndex = 0;
    let wizardPhotoIndex = 0;
    let wizardPhotoCount = 0;
    let wizardStarted = false;

    function currentTuningPayload() {
        const tuning = {};
        colourTuningFields.forEach(field => { tuning[fieldKey(field)] = Number(byId(field).value); });
        hueAnchorFields.forEach(field => { tuning[field] = hueAnchors[field]; });
        return { WallColourHex: byId("wallColourHex").value, Tuning: tuning };
    }

    // White keeps its original mechanic (a plain red/blue gain push-pull,
    // now ±60 instead of ±50 -- 20% more range, on request); each of the six
    // primary/secondary steps instead shows that one colour's own three
    // anchor sliders (hue/brightness/intensity, see hueStepSpecs); each
    // finetuning step shows two colours' worth of those same three-slider
    // groups side by side, refining the same anchors with real-photo context.
    const wizardWhiteRange = 60;

    function anchorControlHtml(colourName, idPrefix) {
        const spec = hueStepSpecs[colourName];
        return `<div style="border-left:3px solid currentColor;padding-left:.8em;margin-bottom:1em">
            <strong>${colourName}</strong>
            <div class="inputContainer">
                <input is="emby-input" id="${idPrefix}Hue" type="range" min="-21" max="21" step="1" label="Hue" />
                <div class="fieldDescription">${spec.lowLabel} &harr; <strong id="${idPrefix}HueValue"></strong> &harr; ${spec.highLabel}</div>
            </div>
            <div class="inputContainer">
                <input is="emby-input" id="${idPrefix}Brightness" type="range" min="50" max="100" step="1" label="Brightness" />
                <div class="fieldDescription">Now: <strong id="${idPrefix}BrightnessValue"></strong></div>
            </div>
            <div class="inputContainer">
                <input is="emby-input" id="${idPrefix}Intensity" type="range" min="50" max="100" step="1" label="Intensity" />
                <div class="fieldDescription">Now: <strong id="${idPrefix}IntensityValue"></strong></div>
            </div>
        </div>`;
    }

    function wireAnchorControl(colourName, idPrefix) {
        const spec = hueStepSpecs[colourName];
        const hueField = `${colourName}HueShiftDegrees`;
        const brightnessField = `${colourName}BrightnessPercent`;
        const intensityField = `${colourName}IntensityPercent`;

        const hueInput = byId(`${idPrefix}Hue`);
        const brightnessInput = byId(`${idPrefix}Brightness`);
        const intensityInput = byId(`${idPrefix}Intensity`);

        // The stored value is the true physical hue-shift direction; the
        // slider the operator sees always runs low-label..high-label as
        // worded, which for Green and Magenta is the opposite physical
        // direction (see hueStepSpecs) -- sign flips it back for display,
        // and flips it again when reading the slider back out below.
        hueInput.value = hueAnchors[hueField] * spec.sign;
        brightnessInput.value = hueAnchors[brightnessField];
        intensityInput.value = hueAnchors[intensityField];

        const updateHueLabel = () => {
            const value = Number(hueInput.value);
            byId(`${idPrefix}HueValue`).textContent = value === 0 ? "centred" : (value > 0 ? `${spec.highLabel} (${value})` : `${spec.lowLabel} (${-value})`);
        };
        updateHueLabel();
        hueInput.addEventListener("input", () => {
            hueAnchors[hueField] = Number(hueInput.value) * spec.sign;
            updateHueLabel();
            retune();
        });

        const updateBrightnessLabel = () => { byId(`${idPrefix}BrightnessValue`).textContent = `${brightnessInput.value}%`; };
        updateBrightnessLabel();
        brightnessInput.addEventListener("input", () => {
            hueAnchors[brightnessField] = Number(brightnessInput.value);
            updateBrightnessLabel();
            retune();
        });

        const updateIntensityLabel = () => { byId(`${idPrefix}IntensityValue`).textContent = `${intensityInput.value}%`; };
        updateIntensityLabel();
        intensityInput.addEventListener("input", () => {
            hueAnchors[intensityField] = Number(intensityInput.value);
            updateIntensityLabel();
            retune();
        });

        addStepButtons(hueInput);
        addStepButtons(brightnessInput);
        addStepButtons(intensityInput);
    }

    function renderStepControls(colourName) {
        const container = byId("wizardStepControls");

        // "White level" is the finetuning replay of the White tuning step
        // against the operator's own real white-level photos instead of the
        // synthetic swatch -- same control, same underlying red/blue gain
        // push-pull, just shown again later with real-photo context.
        if (colourName === "White" || colourName === "White level") {
            container.innerHTML = `<div class="inputContainer"><input is="emby-input" id="wizardQuickField" type="range" min="-${wizardWhiteRange}" max="${wizardWhiteRange}" step="1" label="Colour temperature" /><div class="fieldDescription">Cooler &harr; <strong id="wizardQuickValue"></strong> &harr; Warmer</div></div>`;
            const quick = byId("wizardQuickField");
            quick.value = Math.round((Number(byId("redGainPercent").value) - Number(byId("blueGainPercent").value)) / 2);
            const updateLabel = () => {
                const value = Number(quick.value);
                byId("wizardQuickValue").textContent = value === 0 ? "centred" : (value > 0 ? `Warmer (${value})` : `Cooler (${-value})`);
            };
            updateLabel();
            quick.addEventListener("input", () => {
                const delta = Number(quick.value);
                byId("redGainPercent").value = Math.min(100 + wizardWhiteRange, Math.max(100 - wizardWhiteRange, 100 + delta));
                byId("blueGainPercent").value = Math.min(100 + wizardWhiteRange, Math.max(100 - wizardWhiteRange, 100 - delta));
                updateLabel();
                retune();
            });
            addStepButtons(quick);
            return;
        }

        if (hueStepSpecs[colourName]) {
            container.innerHTML = anchorControlHtml(colourName, "wizardAnchor");
            wireAnchorControl(colourName, "wizardAnchor");
            return;
        }

        const pair = finetuningColourPairs[colourName];
        if (pair) {
            container.innerHTML = pair.map((colour, index) => anchorControlHtml(colour, `wizardAnchor${index}`)).join("");
            pair.forEach((colour, index) => wireAnchorControl(colour, `wizardAnchor${index}`));
            return;
        }

        container.innerHTML = "";
    }

    // A compact, read-only visualisation of the calibrated curve: the six
    // anchors plotted on the colour wheel at their own (possibly shifted)
    // angle, with distance from centre standing in for that anchor's own
    // brightness multiplier. Viewable any time via "Show my colour
    // calibration", not only right after finishing the wizard.
    function renderCurveChart() {
        const container = byId("wizardCurveChart");
        if (!container) {
            return;
        }

        const size = 260;
        const center = size / 2;
        const wheelRadius = 90;
        const wedgeColours = { Red: "#e6483c", Yellow: "#d6c22e", Green: "#4caf50", Cyan: "#26a69a", Blue: "#4a6fd6", Magenta: "#c04fb0" };
        const anchorAngle = { Red: 0, Yellow: 60, Green: 120, Cyan: 180, Blue: 240, Magenta: 300 };
        const toXY = (angleDegrees, radius) => {
            const rad = ((angleDegrees - 90) * Math.PI) / 180;
            return [center + (radius * Math.cos(rad)), center + (radius * Math.sin(rad))];
        };

        let svg = `<svg width="${size}" height="${size}" viewBox="0 0 ${size} ${size}" style="max-width:100%;height:auto">`;
        hueAnchorColours.forEach(colour => {
            const startAngle = anchorAngle[colour] - 30;
            const endAngle = anchorAngle[colour] + 30;
            const [x1, y1] = toXY(startAngle, wheelRadius);
            const [x2, y2] = toXY(endAngle, wheelRadius);
            svg += `<path d="M${center},${center} L${x1.toFixed(1)},${y1.toFixed(1)} A${wheelRadius},${wheelRadius} 0 0 1 ${x2.toFixed(1)},${y2.toFixed(1)} Z" fill="${wedgeColours[colour]}" opacity="0.2" />`;
        });
        hueAnchorColours.forEach(colour => {
            const hueShift = hueAnchors[`${colour}HueShiftDegrees`] || 0;
            const brightness = (hueAnchors[`${colour}BrightnessPercent`] ?? 100) / 100;
            const angle = anchorAngle[colour] + hueShift;
            const radius = wheelRadius * Math.max(0.35, Math.min(1.3, brightness));
            const [x, y] = toXY(angle, radius);
            const [lx, ly] = toXY(anchorAngle[colour], wheelRadius + 24);
            svg += `<line x1="${center}" y1="${center}" x2="${x.toFixed(1)}" y2="${y.toFixed(1)}" stroke="${wedgeColours[colour]}" stroke-width="1.5" opacity="0.55" />`;
            svg += `<circle cx="${x.toFixed(1)}" cy="${y.toFixed(1)}" r="6" fill="${wedgeColours[colour]}" stroke="currentColor" stroke-width="1" />`;
            svg += `<text x="${lx.toFixed(1)}" y="${ly.toFixed(1)}" font-size="11" text-anchor="middle" fill="currentColor">${colour}</text>`;
        });
        svg += "</svg>";
        container.innerHTML = svg;
    }

    function toggleCurveChart() {
        renderCurveChart();
        byId("wizardCurveChartContainer").hidden = !byId("wizardCurveChartContainer").hidden;
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
        if (isLastStep) {
            renderCurveChart();
            byId("wizardCurveChartContainer").hidden = false;
        }
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

    function setWledSmoothingLabel() {
        const value = Number(byId("wledSmoothingMilliseconds").value);
        byId("wledSmoothingValue").textContent = value === 0 ? "0 ms (off, fully reactive)" : `${value} ms`;
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

    // Only known while the device is (or recently was) an active session --
    // Jellyfin's plain device registry has no address of its own, only a
    // session's RemoteEndPoint does. Absent for a device that has not
    // connected since this page loaded its device list.
    function targetDeviceIp() {
        const select = byId("targetDeviceId");
        return select.value ? (knownDevices.find(device => device.id === select.value)?.ip || "") : "";
    }

    function deviceLabelWithIp() {
        const name = targetDeviceName();
        if (!name) {
            return "No device bound yet";
        }

        const ip = targetDeviceIp();
        return ip ? `${name} (${ip})` : name;
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
            // RemoteEndPoint is "ip:port" for most clients; only the host
            // part is meaningful to show next to a device's name.
            const ip = (session.RemoteEndPoint || "").split(":")[0] || existing?.ip || "";
            merged.set(session.DeviceId, {
                id: session.DeviceId,
                name: session.DeviceName || existing?.name || "Unnamed device",
                app: session.Client || existing?.app || "",
                lastUsed: session.LastActivityDate || existing?.lastUsed || "",
                connected: true,
                ip
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
        const fpsCap = byId("wledFpsCapStatus");
        const timeoutStatus = byId("wledTimeoutStatus");
        if (!connection) {
            abl.textContent = "";
            fpsCap.textContent = "";
            timeoutStatus.textContent = "";
            show("wledRgbwModeWarning", false);
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

                const ledFps = settings?.LedFramesPerSecond ?? settings?.ledFramesPerSecond;
                const outputFps = Number(byId("outputFramesPerSecond").value) || 30;
                fpsCap.textContent = ledFps
                    ? outputFps > ledFps
                        ? `WLED's own refresh cap: ${ledFps} fps — lower than the ${outputFps} fps this plugin is sending; raising "LED updates per second" further will not help.`
                        : `WLED's own refresh cap: ${ledFps} fps.`
                    : "";

                const timeoutMs = settings?.RealtimeTimeoutMilliseconds ?? settings?.realtimeTimeoutMilliseconds;
                timeoutStatus.textContent = timeoutMs
                    ? `WLED reclaims the strip after ${(timeoutMs / 1000).toFixed(1)} s without new data.`
                    : "";

                const rgbwMisconfigured = Boolean(settings && (settings.RgbwModeIsMisconfigured ?? settings.rgbwModeIsMisconfigured));
                show("wledRgbwModeWarning", rgbwMisconfigured);
            })
            .catch(() => {
                show("maxBrightnessWarning", false);
                show("rgbwSuggestion", false);
                show("wledRgbwModeWarning", false);
                abl.textContent = "";
                fpsCap.textContent = "";
                timeoutStatus.textContent = "";
            });
    }

    function fixRgbwMode() {
        const connection = currentWledConnection();
        const status = byId("fixRgbwModeStatus");
        if (!connection) {
            return Promise.resolve();
        }

        byId("fixRgbwMode").disabled = true;
        status.textContent = "Setting to Manual…";
        return window.ApiClient.ajax({
            type: "POST",
            url: window.ApiClient.getUrl("RealtimeAmbilight/Discovery/FixRgbwMode", connection)
        }).then(() => {
            status.textContent = "Done. Checking WLED again…";
            return checkControllerSettings();
        }).then(() => {
            status.textContent = byId("wledRgbwModeWarning").style.display !== "none"
                ? "WLED still reports a different mode; you may need to change it in WLED directly."
                : "Fixed.";
        }).catch(error => {
            status.textContent = error?.status === 403
                ? "Turn on \"Allow this plugin to fix WLED settings\" above, save, and try again."
                : (error?.responseText || "WLED did not accept the change.");
        }).finally(() => { byId("fixRgbwMode").disabled = false; });
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
                latestWledStatus = result;
                const online = result?.IsOnline ?? result?.isOnline;
                const realtime = result?.IsRealtimeActive ?? result?.isRealtimeActive;
                status.textContent = online
                    ? realtime
                        ? "● Online — realtime Ambilight is active."
                        : "● Online — WLED is ready."
                    : "● Not reachable — check address, port and network.";
                refreshOverviewCards();
            })
            .catch(() => {
                latestWledStatus = null;
                status.textContent = "● Not reachable — check address, port and network.";
            });
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
                resetHueAnchors(config);
                byId("enabled").checked = config.Enabled !== false;
                byId("holdWhilePaused").checked = config.HoldWhilePaused !== false;
                byId("ignoreBlackBorders").checked = config.IgnoreBlackBorders !== false;
                byId("correctLedGamma").checked = config.CorrectLedGamma !== false;
                byId("autoDetectLedGamma").checked = config.AutoDetectLedGamma !== false;
                byId("allowWledControl").checked = config.AllowWledControl === true;
                byId("sendWhiteChannel").checked = config.SendWhiteChannel === true;
                byId("whiteChannelStrengthPercent").value = config.WhiteChannelStrengthPercent ?? 50;
                byId("hueEnabled").checked = config.HueEnabled === true;
                byId("hueBrightnessPercent").value = config.HueBrightnessPercent || 100;
                byId("hueResponsePercent").value = config.HueResponsePercent ?? 50;
                // Jellyfin's plugin configuration API returns an enum as its
                // string name ("WarmWhiteDim"), not the numeric value the
                // <select>'s own options use -- the same mismatch already
                // handled for realtimeProtocol above. Setting a <select>'s
                // value to a string with no matching <option> just leaves it
                // blank, which is exactly what was reported: the saved
                // choice reads back empty after a refresh.
                byId("hueEndBehaviour").value = String({ WarmWhiteDim: 0, RestorePreviousState: 1 }[config.HueEndBehaviour] ?? config.HueEndBehaviour ?? 0);
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
                setWledSmoothingLabel();
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
            .then(() => {
                refreshOverviewCards();
                showOverview();
            })
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
            WhiteChannelStrengthPercent: Number(byId("whiteChannelStrengthPercent").value),
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
            HueResponsePercent: Number(byId("hueResponsePercent").value),
            HueEndBehaviour: Number(byId("hueEndBehaviour").value)
        };
        numericFields.forEach(field => { config[fieldKey(field)] = Number(byId(field).value); });
        hueAnchorFields.forEach(field => { config[field] = hueAnchors[field]; });
        if (hostPort) {
            config.WledHttpPort = Number(hostPort);
        }

        window.ApiClient.updatePluginConfiguration(pluginId, config)
            .then(result => {
                loadedConfig = config;
                refreshOverviewCards();
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
                latestHueStatus = status;
                const state = status.State ?? status.state;
                const issue = status.Issue ?? status.issue;
                const paired = status.Paired ?? status.paired;
                const bridgeHost = status.BridgeHost ?? status.bridgeHost ?? "";
                byId("hueStatusLine").textContent = paired
                    ? describeHueState(state, issue)
                    : "Not paired. Scan for a bridge below to get started.";
                show("hueSelectionSection", paired);
                // "Scan the network for a bridge" only makes sense before
                // pairing; once paired, the only relevant bridge action is to
                // unlink it (moved up next to the status line, as a plain
                // link rather than another raised button competing with it).
                show("huePairingSection", !paired);
                byId("hueUnlink").style.display = paired ? "inline" : "none";
                byId("hueUnlink").textContent = bridgeHost ? `Unlink bridge ${bridgeHost}` : "Unlink this bridge";
                byId("hueStartPairing").textContent = paired ? "Bridge paired" : "Pair with this bridge";
                if (paired && !hueSelectedBridgeHost) {
                    hueSelectedBridgeHost = bridgeHost;
                }

                return paired;
            })
            .catch(() => { latestHueStatus = null; byId("hueStatusLine").textContent = ""; return false; });
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
        const totalSeconds = 30;
        let attemptsLeft = 15;
        // A live countdown, not just a static "press the button" label --
        // it tells the operator exactly how long they actually have left to
        // walk to the bridge, updated every second independently of the 2 s
        // poll cycle so it counts down smoothly rather than jumping in pairs.
        let secondsLeft = totalSeconds;
        const updateCountdown = () => { button.textContent = `Press the button on the bridge now… (${secondsLeft}s)`; };
        updateCountdown();
        byId("huePairingStatus").textContent = "";
        const countdown = setInterval(() => {
            secondsLeft = Math.max(0, secondsLeft - 1);
            updateCountdown();
        }, 1000);
        const stopCountdown = () => clearInterval(countdown);

        const attempt = () => window.ApiClient.ajax({
            type: "POST",
            url: window.ApiClient.getUrl("RealtimeAmbilight/Hue/Pair", { host }),
            dataType: "json",
        }).then(result => {
            const success = result.Success ?? result.success;
            if (success) {
                stopCountdown();
                button.textContent = "Bridge paired";
                button.disabled = false;
                byId("huePairingStatus").textContent = "";
                return Promise.all([refreshLoadedConfig(), loadHueStatus(), refreshHueEntertainmentConfigs()]);
            }

            attemptsLeft--;
            const reason = result.FailureReason ?? result.failureReason ?? "";
            if (attemptsLeft <= 0) {
                stopCountdown();
                button.textContent = "Pair with this bridge";
                byId("huePairingStatus").textContent = `Gave up: ${reason || "the bridge did not respond in time"}.`;
                button.disabled = false;
                return null;
            }

            byId("huePairingStatus").textContent = reason || "Waiting for the link button…";
            return new Promise(resolve => setTimeout(resolve, 2000)).then(attempt);
        }).catch(() => {
            stopCountdown();
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
                    select.add(new Option(`${name} (${channelCount} light${channelCount === 1 ? "" : "s"})`, id));
                });
                if (previousValue) {
                    select.value = previousValue;
                }

                show("hueSelectionSection", true);
            })
            .catch(() => {});
    }

    function unlinkHue(event) {
        event.preventDefault();
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

    function setHueResponseLabel() {
        const value = Number(byId("hueResponsePercent").value);
        byId("hueResponseValue").textContent = value === 50
            ? "Balanced (50)"
            : value < 50 ? `Reactive (${value})` : `Smooth (${value})`;
    }

    populateWallColourPresets();
    createSideTuningCards();
    addRangeScales();
    addStepButtonsToSection();
    // Delegated, not a per-element listener: some [data-open-tab] elements
    // (e.g. the "Open TV settings" link inside the FPS chain hint) are
    // (re)created after this point by innerHTML writes, so binding once at
    // init would silently miss them.
    view.addEventListener("click", event => {
        const opener = event.target.closest("[data-open-tab]");
        if (opener) {
            switchTab(opener.dataset.openTab);
        }
    });
    view.querySelectorAll("[data-open-overview]").forEach(link => link.addEventListener("click", showOverview));
    view.querySelectorAll("[data-edit-toggle]").forEach(button => button.addEventListener("click", () => showSectionEdit(button.dataset.editToggle)));
    setInterval(refreshFpsChain, 2000);
    // Slower: these each make a real network call to WLED/Hue, not just a
    // cheap in-process counter read like refreshFpsChain -- only needed
    // often enough for the overview pills to feel live, not every tick.
    setInterval(() => { checkControllerStatus(); loadHueStatus().then(refreshOverviewCards); }, 8000);
    view.addEventListener("viewshow", load);
    byId("realtimeAmbilightConfigurationForm").addEventListener("submit", save);
    byId("outputDelayMilliseconds").addEventListener("input", setDelayLabel);
    byId("samplingDepthPercent").addEventListener("input", setDepthLabel);
    byId("minimumColourHoldMilliseconds").addEventListener("input", setMinimumColourHoldLabel);
    byId("wledSmoothingMilliseconds").addEventListener("input", setWledSmoothingLabel);
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
    byId("showCurveChart").addEventListener("click", toggleCurveChart);
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
    byId("whiteChannelStrengthPercent").addEventListener("input", setColourLabels);
    byId("sendWhiteChannel").addEventListener("change", setColourLabels);
    byId("allowWledControl").addEventListener("change", () => {
        byId("allowWledControlSummary").textContent = byId("allowWledControl").checked
            ? "This plugin may fix WLED settings for you. Save to keep it that way."
            : "This plugin only reads WLED until you turn this on.";
    });
    byId("fixForceMaxBrightness").addEventListener("click", fixForceMaxBrightness);
    byId("fixRgbwMode").addEventListener("click", fixRgbwMode);
    byId("hueScanBridges").addEventListener("click", scanHueBridges);
    byId("hueStartPairing").addEventListener("click", startHuePairing);
    byId("hueRefreshConfigs").addEventListener("click", refreshHueEntertainmentConfigs);
    byId("hueUnlink").addEventListener("click", unlinkHue);
    byId("hueBrightnessPercent").addEventListener("input", setHueBrightnessLabel);
    byId("hueResponsePercent").addEventListener("input", setHueResponseLabel);
}
