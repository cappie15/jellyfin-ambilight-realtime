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
    // Each anchor's own canonical hue, shared by the colour-deviation grid's
    // swatch dots and the curve chart's wedges/lines -- one source so the two
    // views of the same six colours can never drift apart.
    const wedgeColours = { Red: "#e6483c", Yellow: "#d6c22e", Green: "#4caf50", Cyan: "#26a69a", Blue: "#4a6fd6", Magenta: "#c04fb0" };

    // Mirrors CalibrationWizard.SwatchColours server-side (60% value, not
    // 100% -- a fully saturated colour at maximum brightness is rare in real
    // playback content). Purely descriptive, so the operator can see exactly
    // what is being sent to the TV for this step rather than guessing.
    const wizardSwatchColours = {
        White: [153, 153, 153], Grey20: [51, 51, 51], Grey40: [102, 102, 102],
        Red: [153, 0, 0], Yellow: [153, 153, 0], Green: [0, 153, 0],
        Cyan: [0, 153, 153], Blue: [0, 0, 153], Magenta: [153, 0, 153],
    };
    // "White" reads fine as-is; the two grey steps read much better as "20%
    // grey"/"40% grey" than the bare identifier used for routing/state.
    const wizardSwatchDisplayNames = { Grey20: "20% grey", Grey40: "40% grey" };
    // Above this, a reachable device's own ping figure (and that card's state
    // label) turns amber -- still "there", just slow enough to call out. WLED
    // and the Hue bridge are both LAN devices normally answering in a few ms,
    // so this is a generous margin, not a hair trigger.
    const SlowPingThresholdMs = 15;
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

    // The wizard's 20%/40%-grey steps: an independent temperature+tint
    // correction at each of those two brightness levels, on top of White's
    // own flat gain (see Core.Color.GreyBalanceCurve). Kept in their own
    // small state object for the same reason hueAnchors is: no matching
    // Advanced-tab raw slider exists for these either.
    const greyBalanceFields = ["Grey20TemperatureDelta", "Grey20TintDelta", "Grey40TemperatureDelta", "Grey40TintDelta"];
    let greyBalance = {};
    function resetGreyBalance(source) {
        greyBalance = {};
        greyBalanceFields.forEach(field => { greyBalance[field] = Number(source?.[field] ?? 0); });
    }
    resetGreyBalance(null);

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

    const numericFields = [
        "wledHttpPort", "realtimeProtocol", "outputDelayMilliseconds", "outputFramesPerSecond",
        "topLedCount", "rightLedCount", "bottomLedCount", "leftLedCount", "samplingDepthPercent",
        "minimumColourHoldMilliseconds", "wledSmoothingMilliseconds", ...colourTuningFields
    ];
    // The plugin's own factory-default configuration, fetched once from the
    // server (RealtimeAmbilight/Discovery/DefaultConfiguration) rather than
    // duplicated here by hand -- a hand-maintained copy of PluginConfiguration's
    // property initializers drifted out of sync with them more than once
    // (this project's own history: BrightnessPercent, SaturationPercent,
    // SamplingDepthPercent and several others were still the old numbers here
    // long after the real defaults changed), silently resetting a field to a
    // stale value instead of its actual current default. Populated by load()
    // before anything reads it; every reset button below depends on it.
    let factoryDefaults = null;
    // Soft, muted, contemporary interior tones -- the kind of wall colour
    // actually behind a TV today (warm off-whites, greiges, dusty sage and
    // blue, soft clay) -- not swatches sampled evenly across the full colour
    // wheel. Saturated primaries are deliberately absent.
    const wallColourPresets = [
        { label: "White, no correction", hex: "#ffffff" },
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
    // { reachable, milliseconds } | null per device, refreshed by refreshPings()
    // every 5 s and rendered inline in each overview card by refreshOverviewCards.
    let latestPings = { wled: null, hue: null };
    // False only for the brief window between the page's first paint and the
    // first ping round trip resolving. Without this, a configured-but-not-yet-
    // pinged WLED/Hue card reads latestPings.wled/hue as null and falls
    // through to the same branch as "reachable", flashing a green READY that
    // flips to red UNREACHABLE a moment later once the real check lands --
    // confirmed live, this is not what a working state actually looks like.
    let pingsEverLoaded = false;
    const byId = id => view.querySelector(`#${id}`);
    const fieldKey = field => field[0].toUpperCase() + field.slice(1);
    const show = (id, visible) => { byId(id).style.display = visible ? "block" : "none"; };
    const escapeHtml = text => String(text).replace(/[&<>"']/g, char => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#39;" }[char]));

    // The two clips ship embedded in the plugin itself and are extracted
    // and registered, alongside the operator's own custom-clips folder, as a
    // real Jellyfin library on the operator's own click (TestVideoLibrary,
    // server-side) -- no Dashboard trip, no manual library of their own to
    // build. A real library, not just files on disk: the Ambilight pipeline
    // only ever reacts to an actual Jellyfin playback session, so the clips
    // have to be a real, playable library item.
    function renderTestVideoRows(items, customClipsFolderPath) {
        const serverId = window.ApiClient.serverId?.() ?? "";
        const rows = items.slice().sort((a, b) => a.Name.localeCompare(b.Name)).map(item => {
            const playUrl = `${window.location.origin}/web/#/details?id=${encodeURIComponent(item.Id)}&serverId=${encodeURIComponent(serverId)}`;
            return `<div class="raTestVideoRow"><span>${escapeHtml(item.Name)}</span><a is="emby-linkbutton" href="${playUrl}" target="_blank" rel="noopener">Play &#9656;</a></div>`;
        }).join("");
        const notice = items.length === 0
            ? "<p class=\"fieldDescription\">Set up just now; Jellyfin is still scanning. Close and reopen this in a few seconds.</p>"
            : "";
        return `${rows}${notice}`
            + `<p class="fieldDescription" style="margin-top:.6em">Want more variety? Drop your own clips (that you hold the rights to -- a sunrise, sports, a film, nature footage) into <code>${escapeHtml(customClipsFolderPath)}</code> on the server, then rescan.</p>`
            + `<div class="raTestVideoRow"><button type="button" id="rescanTestVideos" class="raLink">Rescan for new clips</button><span class="fieldDescription">Done tuning?</span><button type="button" id="removeTestVideos" class="raLink">Remove test videos</button></div>`;
    }

    function findTestVideoItems() {
        return window.ApiClient.getJSON(window.ApiClient.getUrl("Items", {
            searchTerm: "Ambilight test",
            Recursive: true,
            IncludeItemTypes: "Video",
            Limit: 20
        })).then(result => result?.Items ?? []);
    }

    function wireRemoveTestVideosButton() {
        byId("removeTestVideos")?.addEventListener("click", () => {
            byId("testVideosPanel").innerHTML = "<p class=\"fieldDescription\">Removing&hellip;</p>";
            window.ApiClient.ajax({
                type: "POST",
                url: window.ApiClient.getUrl("RealtimeAmbilight/Calibration/TestVideos/Remove")
            }).then(loadTestVideos).catch(() => {
                byId("testVideosPanel").innerHTML = "<p class=\"fieldDescription\">Could not remove the test videos right now.</p>";
            });
        });
    }

    // Setup is idempotent: once already set up it just rescans, which is
    // also what "I dropped new clips into my own folder" needs -- one
    // endpoint, one button label change, no separate rescan-only route.
    function wireSetupTestVideosButton(alreadySetUp) {
        const button = byId(alreadySetUp ? "rescanTestVideos" : "setupTestVideos");
        button?.addEventListener("click", () => {
            byId("testVideosPanel").innerHTML = alreadySetUp
                ? "<p class=\"fieldDescription\">Rescanning&hellip;</p>"
                : "<p class=\"fieldDescription\">Setting up&hellip; extracting the clips and scanning the new library, this takes a few seconds.</p>";
            window.ApiClient.ajax({
                type: "POST",
                url: window.ApiClient.getUrl("RealtimeAmbilight/Calibration/TestVideos/Setup")
            }).then(loadTestVideos).catch(() => {
                byId("testVideosPanel").innerHTML = "<p class=\"fieldDescription\">Could not reach the test-video library right now.</p>";
            });
        });
    }

    // Re-run every time the panel opens rather than cached: whether the
    // library is set up, and whether its scan has caught up yet, can both
    // change between one open and the next.
    function loadTestVideos() {
        const panel = byId("testVideosPanel");
        panel.innerHTML = "<p class=\"fieldDescription\">Loading&hellip;</p>";
        window.ApiClient.getJSON(window.ApiClient.getUrl("RealtimeAmbilight/Calibration/TestVideos/Status")).then(status => {
            const isSetUp = status?.IsSetUp ?? status?.isSetUp ?? false;
            const customClipsFolderPath = status?.CustomClipsFolderPath ?? status?.customClipsFolderPath ?? "";
            if (!isSetUp) {
                panel.innerHTML = `<p class="fieldDescription">Two short synthetic clips (4:3 and 21:9 bars) for testing the black-border detector -- geometry no real footage can guarantee. One click adds them, plus your own folder for real content, as their own temporary library; nothing else on your server is touched.</p>`
                    + `<p class="fieldDescription">Want to tune against real content too (a sunrise, sports, a film, nature footage)? This plugin can't ship or download that for you -- copyrighted video can't be bundled into a public plugin -- but drop in clips you already hold the rights to and this same library will include them. The folder is created for you once you set up: <code>${escapeHtml(customClipsFolderPath)}</code>.</p>`
                    + "<button type=\"button\" id=\"setupTestVideos\" class=\"raised\">Set up test videos</button>";
                wireSetupTestVideosButton(false);
                return;
            }

            findTestVideoItems().then(items => {
                panel.innerHTML = renderTestVideoRows(items, customClipsFolderPath);
                wireRemoveTestVideosButton();
                wireSetupTestVideosButton(true);
            });
        }).catch(() => {
            panel.innerHTML = "<p class=\"fieldDescription\">Could not check the test videos right now.</p>";
        });
    }

    function toggleTestVideosPanel() {
        const panel = byId("testVideosPanel");
        panel.hidden = !panel.hidden;
        if (!panel.hidden) {
            loadTestVideos();
        }
    }

    // Polled only while the panel is actually open (see toggleLivePreviewPanel),
    // and stopped again on close/page-hide -- explicitly requested as a
    // lighter alternative to HyperHDR's own live LED view, so it must not add
    // a poll that runs forever once loaded.
    let livePreviewTimer = null;

    function renderLivePreviewSide(elementId, hexColours) {
        const el = byId(elementId);
        if (!hexColours || hexColours.length === 0) {
            el.innerHTML = "";
            return;
        }

        el.innerHTML = hexColours.map(hex => `<span style="background:${hex}"></span>`).join("");
    }

    function loadLivePreview() {
        window.ApiClient.getJSON(window.ApiClient.getUrl("RealtimeAmbilight/Discovery/LivePreview")).then(preview => {
            const top = preview?.Top ?? preview?.top;
            const right = preview?.Right ?? preview?.right;
            const bottom = preview?.Bottom ?? preview?.bottom;
            const left = preview?.Left ?? preview?.left;
            if (!top && !right && !bottom && !left) {
                ["raLivePreviewTop", "raLivePreviewRight", "raLivePreviewBottom", "raLivePreviewLeft"].forEach(id => renderLivePreviewSide(id, null));
                byId("raLivePreviewStatus").textContent = "Waiting for playback…";
                return;
            }

            renderLivePreviewSide("raLivePreviewTop", top);
            renderLivePreviewSide("raLivePreviewRight", right);
            renderLivePreviewSide("raLivePreviewBottom", bottom);
            renderLivePreviewSide("raLivePreviewLeft", left);
            byId("raLivePreviewStatus").textContent = "Live.";
        }).catch(() => {
            byId("raLivePreviewStatus").textContent = "Could not reach the preview right now.";
        });
    }

    // A Hue entertainment channel's reported position says where the bridge
    // thinks a light is, which stops being the whole story the moment a
    // gradient strip is physically shortened or a set hangs above the
    // bridge's own origin. Lighting one channel at a time and looking is the
    // only way to settle it, and working that out by hand has cost this
    // project three separate rounds.
    function renderHueIdentifyButtons(channelCount) {
        const row = byId("hueIdentifyButtons");
        const buttons = [];
        for (let channel = 0; channel < channelCount; channel++) {
            buttons.push(`<button type="button" is="emby-button" class="raised raHueIdentify" data-channel="${channel}">${channel}</button>`);
        }

        buttons.push('<button type="button" is="emby-button" class="raised raHueIdentify" data-channel="">All off</button>');
        row.innerHTML = buttons.join("");
        row.querySelectorAll(".raHueIdentify").forEach(button => {
            button.addEventListener("click", () => identifyHueChannel(button.getAttribute("data-channel")));
        });
    }

    function identifyHueChannel(channel) {
        const status = byId("hueIdentifyStatus");
        const query = channel === "" ? "" : `?channel=${encodeURIComponent(channel)}`;
        status.textContent = channel === "" ? "Clearing…" : `Lighting channel ${channel}…`;
        window.ApiClient.ajax({ type: "POST", url: window.ApiClient.getUrl(`RealtimeAmbilight/Hue/Identify${query}`) })
            .then(() => {
                status.textContent = channel === ""
                    ? "Cleared. The lights follow the picture again."
                    : `Channel ${channel} is lit, every other channel is black. Note where you see it.`;
            })
            .catch(() => {
                status.textContent = "Nothing is streaming to Hue right now. Start playback on the bound television first.";
            });
    }

    function toggleLivePreviewPanel() {
        const panel = byId("livePreviewPanel");
        panel.hidden = !panel.hidden;
        if (panel.hidden) {
            clearInterval(livePreviewTimer);
            livePreviewTimer = null;
            return;
        }

        loadLivePreview();
        livePreviewTimer = setInterval(loadLivePreview, 700);
    }

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
        const hueBlackFloor = Number(byId("hueBlackLevelFloorPercent").value);
        byId("hueBlackLevelFloorValue").textContent = hueBlackFloor === 0 ? "0% (off)" : `${hueBlackFloor}%`;
        byId("hueScreenWidthValue").textContent = `${byId("hueScreenWidthPercent").value}%`;
        byId("hueScreenHeightValue").textContent = `${byId("hueScreenHeightPercent").value}%`;
        byId("hueScreenCentreHeightValue").textContent = `${byId("hueScreenCentreHeightPercent").value}%`;
        byId("hueScreenCentreWidthValue").textContent = `${byId("hueScreenCentreWidthPercent").value}%`;
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

    // Card overview is the landing page: every card always shows its own
    // compact, read-only summary (name, state, one headline stat, one
    // caption) -- there is nothing to expand or collapse any more. Every
    // card's own "Edit" button opens that section's own modal (see
    // openEditModal) -- settings are split one modal per section rather
    // than one long shared page.
    const sectionSummarisers = {};

    // Ready / Streaming / Calibrated all read as the same "working" green;
    // only the label text (passed in by the caller) tells them apart. Slow
    // (reachable, high latency) and Off/Unreachable are the only states that
    // are not green -- see the CSS remarks on .raCardState.
    function setCardState(prefix, state, label) {
        const el = byId(`${prefix}State`);
        if (!el) {
            return;
        }

        el.className = `raCardState ra${state[0].toUpperCase()}${state.slice(1)}`;
        el.textContent = label;
    }

    // The one shared bar spanning all four cards: solid green whenever
    // Ambilight is enabled, rotating through an Ambilight-style colour sweep
    // only while something is actually streaming right now, and a plain grey
    // when the master switch is off. This is a whole-panel signal ("is
    // anything alive"), independent of any one card's own state.
    function updateRackBar() {
        const bar = byId("raRackBar");
        if (!bar) {
            return;
        }

        const enabled = byId("enabled").checked;
        const streaming = isWledStreaming() || isHueStreaming();
        bar.classList.toggle("raRackOff", !enabled);
        bar.classList.toggle("raRackSweep", enabled && streaming);
    }

    // Settings are split one modal per section rather than one long shared
    // page -- opening TV's settings should not mean scrolling past WLED,
    // Ambilight and Hue's controls to get there. The overview cards and the
    // signal chain/activity panels sit behind the backdrop, still refreshing
    // -- closing the modal (or just glancing at the pill/state visible
    // through the dimmed backdrop) is enough to see a change take effect.
    let openModalSection = null;

    function openEditModal(section) {
        if (!section) {
            return;
        }

        closeEditModal();
        byId(`editModal-${section}`).hidden = false;
        byId("raModalBackdrop").hidden = false;
        openModalSection = section;
        requestAnimationFrame(updateModalScrollFade);
    }

    // A modal's own content can grow after it opens -- WLED's RGBW checkbox
    // reveals a whole "White LED strength" field, for one -- so this is
    // recomputed on every field change inside the modal (see the input/change
    // listeners below), not just once on open.
    function updateModalScrollFade() {
        if (!openModalSection) {
            return;
        }

        const body = byId(`editModal-${openModalSection}`)?.querySelector(".raModalBody");
        if (body) {
            body.classList.toggle("raScrollable", body.scrollHeight > body.clientHeight + 1);
        }
    }

    function closeEditModal() {
        if (openModalSection) {
            byId(`editModal-${openModalSection}`).hidden = true;
        }

        byId("raModalBackdrop").hidden = true;
        openModalSection = null;
    }

    function isAmbilightCalibrated() {
        return hueAnchorColours.some(colour => hueAnchors[`${colour}HueShiftDegrees`] !== 0)
            || Number(byId("brightnessPercent").value) !== 100;
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
        const enabled = byId("enabled").checked;
        const streaming = isWledStreaming() || isHueStreaming();
        setCardState("tv", enabled ? "ok" : "off", !enabled ? "OFF" : streaming ? "STREAM" : "READY");
        byId("tvStat").textContent = targetDeviceName() || "Not bound yet";
        const ip = targetDeviceIp();
        const width = Math.max(16, Number(byId("analysisWidth").value) || (factoryDefaults && factoryDefaults.AnalysisWidth) || 320);
        byId("tvCap").textContent = `${ip ? `${ip} · ` : ""}${width} × ${analysisHeight()} · Ambilight ${enabled ? "on" : "off"}`;
        const device = knownDevices.find(candidate => candidate.id === byId("targetDeviceId").value);
        byId("tvSub").textContent = device?.lastUsed ? `Last seen ${lastUsedLabel(device.lastUsed)}` : "";
    };

    sectionSummarisers.wled = function summariseWled() {
        const host = byId("wledHost").value.trim() || loadedConfig?.WledHost || "";
        const wledName = latestWledStatus?.Name ?? latestWledStatus?.name;
        const streaming = isWledStreaming();
        const ping = latestPings.wled;
        const state = !host ? "off"
            : !pingsEverLoaded ? "pending"
            : (ping && !ping.reachable) ? "dead"
            : (ping?.reachable && ping.milliseconds > SlowPingThresholdMs) ? "warn"
            : "ok";
        const label = !host ? "OFF" : !pingsEverLoaded ? "CHECKING…" : state === "dead" ? "UNREACHABLE" : state === "warn" ? "SLOW" : streaming ? "STREAM" : "READY";
        setCardState("wled", state, label);
        const counts = ledCountFields.map(field => Number(byId(field).value) || 0);
        byId("wledStat").innerHTML = host ? `${counts.reduce((a, b) => a + b, 0)} <span class="raUnit">LEDs</span>` : "&mdash;";
        const protocol = latestPerformance?.WledProtocol ?? latestPerformance?.wledProtocol;
        byId("wledCap").textContent = host
            ? `${wledName ? `${wledName} (${host})` : host} · ${byId("sendWhiteChannel").checked ? "RGBW" : "RGB"} · ${byId("outputFramesPerSecond").value} fps${protocol ? ` · ${protocol}` : ""}`
            : "Scan or enter a controller address";
        byId("wledPing").innerHTML = pingLine(ping);
    };

    // Always one cell per anchor (a fixed 2-column, 3-row grid), a small dot
    // in that colour's own canonical hue in front of the name -- title
    // attribute carries the hex for a plain-browser tooltip -- and every
    // deviation from the default expressed the same way: a signed number
    // against its own 100% baseline, never a bare absolute percentage, so
    // "-6% int" always means the same kind of thing "+8°" does. Kept to one
    // line per colour no matter what -- see .raColourCell's own CSS remarks.
    function renderColourDeviation() {
        // Clamped to match PerimeterColourTuning.Anchor()'s own server-side
        // range (hue +-23 deg, brightness 20-100%, intensity 50-220%) -- a
        // value saved outside that range by an older build would otherwise
        // show a number here that is not actually what gets applied. This
        // clamp had drifted stale before (still +-21/50-100/50-100 after the
        // server side moved to +-23/20-100/50-220), silently under-reporting
        // every anchor's own real, applied deviation once any of Brightness's
        // widened floor or Intensity's widened ceiling was actually used.
        const anchors = hueAnchorColours.map(colour => ({
            colour,
            hue: Math.max(-23, Math.min(23, hueAnchors[`${colour}HueShiftDegrees`] || 0)),
            brightness: Math.max(20, Math.min(100, hueAnchors[`${colour}BrightnessPercent`] ?? 100)),
            intensity: Math.max(50, Math.min(220, hueAnchors[`${colour}IntensityPercent`] ?? 100))
        }));

        const signed = value => `${value > 0 ? "+" : ""}${value}%`;
        const cellHtml = a => {
            const parts = [];
            if (a.hue !== 0) {
                parts.push(`${a.hue > 0 ? "+" : ""}${a.hue}°`);
            }
            if (a.brightness !== 100) {
                parts.push(`${signed(a.brightness - 100)} bright`);
            }
            if (a.intensity !== 100) {
                parts.push(`${signed(a.intensity - 100)} int`);
            }
            const hex = wedgeColours[a.colour];
            return `<div class="raColourCell"><span class="raColourDot" style="background:${hex}" title="${hex}"></span><strong>${a.colour}</strong>: ${parts.length ? parts.join(", ") : "Default"}</div>`;
        };
        // hueAnchorColours is ["Red", "Green", "Blue", "Yellow", "Cyan", "Magenta"]
        // -- the first three are exactly the primaries, the last three exactly
        // the secondaries, so a plain slice is all grouping this needs.
        const columnHtml = (title, columnAnchors) =>
            `<div class="raColourColumn"><div class="raColourColumnTitle">${title}</div>${columnAnchors.map(cellHtml).join("")}</div>`;
        byId("ambilightSummaryDeviation").innerHTML =
            columnHtml("Primary", anchors.slice(0, 3)) + columnHtml("Secondary", anchors.slice(3, 6));

        const adjustedCount = anchors.filter(a => a.hue !== 0 || a.brightness !== 100 || a.intensity !== 100).length;
        byId("ambilightSummaryDeviationCount").textContent = adjustedCount === 0 ? "None (default curve)" : `${adjustedCount} of 6 adjusted`;
    }

    sectionSummarisers.ambilight = function summariseAmbilight() {
        const calibrated = isAmbilightCalibrated();
        setCardState("ambilight", calibrated ? "ok" : "off", calibrated ? "CALIBRATED" : "DEFAULT");
        byId("ambilightStat").innerHTML = `${byId("brightnessPercent").value}<span class="raUnit">%</span>`;
        renderColourDeviation();
        const deviationSummary = byId("ambilightSummaryDeviationCount").textContent;
        byId("ambilightCap").textContent = `intensity ${byId("saturationPercent").value}% · ${deviationSummary}`;
        const smoothingMs = Number(byId("wledSmoothingMilliseconds").value) || 0;
        byId("ambilightSub").textContent = smoothingMs === 0 ? "Smoothing off (fully reactive)" : `Smoothing ${smoothingMs} ms`;
        // The curve chart is always visible now (not behind a click), so it
        // needs to already be current whenever this card/modal is opened, not
        // only once the wizard itself reaches its last step.
        if (!wizardStarted) {
            renderCurveChart();
        }

        byId("startCalibrationWizard").textContent = calibrated ? "Restart calibration" : "Start calibration";
    };

    sectionSummarisers.hue = function summariseHue() {
        const bridgeHost = loadedConfig?.HueBridgeHost || "";
        const enabled = byId("hueEnabled").checked;
        byId("hueSelectionSection").classList.toggle("raDimmed", !enabled);
        const paired = Boolean(bridgeHost);
        const configured = enabled && paired;
        const streaming = isHueStreaming();
        const ping = latestPings.hue;
        const state = !configured ? "off"
            : !pingsEverLoaded ? "pending"
            : (ping && !ping.reachable) ? "dead"
            : (ping?.reachable && ping.milliseconds > SlowPingThresholdMs) ? "warn"
            : "ok";
        const label = !configured ? "OFF" : !pingsEverLoaded ? "CHECKING…" : state === "dead" ? "UNREACHABLE" : state === "warn" ? "SLOW" : streaming ? "STREAM" : "READY";
        setCardState("hue", state, label);

        const areaOption = byId("hueEntertainmentConfig").selectedOptions[0]?.textContent
            || loadedConfig?.HueEntertainmentConfigurationName
            || "";
        const lightMatch = areaOption.match(/\((\d+) lights?\)/);
        byId("hueStat").innerHTML = lightMatch ? `${lightMatch[1]} <span class="raUnit">lights</span>` : "&mdash;";
        const areaName = areaOption.replace(/\s*\(\d+ lights?\)\s*$/, "") || (paired ? "Not selected" : "Not paired yet");
        byId("hueCap").textContent = `${areaName}${bridgeHost ? ` · ${bridgeHost}` : ""}`;
        byId("huePing").innerHTML = pingLine(ping);
    };

    // Cheap: reads three already-maintained counters, no measuring work of
    // its own on the server. Polled at a modest interval regardless of
    // which section is open, so the overview card's own pill/meta text
    // stays current without needing its own separate refresh path.
    // All eight stages a frame actually passes through, in order -- not just
    // the three (decode/sample/WLED-send) that happen to have their own
    // independent throughput counter. Colour+HDR, LED mapping and smoothing
    // run inline inside the same per-frame WLED call as "Sent to WLED"
    // itself, so they share its own active/inactive state rather than
    // inventing a rate of their own that does not exist. Hue has a genuinely
    // separate send rate (its own 25 Hz pump, independent of WLED), so it
    // gets its own active flag instead of piggy-backing on WLED's.
    function refreshFpsChain() {
        return window.ApiClient.getJSON(window.ApiClient.getUrl("RealtimeAmbilight/Discovery/Performance"))
            .then(perf => {
                latestPerformance = perf;
                const analyse = perf?.AnalyseFps ?? perf?.analyseFps;
                const sample = perf?.SampleFps ?? perf?.sampleFps;
                const wled = perf?.WledRenderFps ?? perf?.wledRenderFps;
                const hue = perf?.HueSendFps ?? perf?.hueSendFps;
                const sourceFps = perf?.SourceFps ?? perf?.sourceFps;
                const active = analyse !== null && analyse !== undefined;
                const hueActive = hue !== null && hue !== undefined;
                const format = value => `${Number(value).toFixed(1)} fps`;

                const setStage = (stageId, valueId, text, isActive) => {
                    byId(valueId).textContent = text;
                    byId(stageId).className = `raStage${isActive ? " raOk" : ""}`;
                };

                setStage("stage-playback", "stagePlayback", active ? "Playing" : "Idle", active);
                byId("stageSourceFps").textContent = active && sourceFps ? `source ${Number(sourceFps).toFixed(3)} fps` : "";
                setStage("stage-decode", "fpsAnalyse", active ? format(analyse) : "Ready", active);
                setStage("stage-sample", "fpsSample", active ? format(sample) : "Ready", active);
                setStage("stage-colour", "stageColour", "inline", active);
                setStage("stage-wled", "fpsWled", active ? format(wled) : "Ready", active);
                setStage("stage-hue", "fpsHue", hueActive ? format(hue) : "Ready", hueActive);

                // Each wire is "on" only once both stages it joins are live,
                // so the colour reads as one signal actually reaching that
                // point in the chain, not a decoration next to it.
                for (let index = 1; index <= 4; index++) {
                    byId(`connector-${index}`).className = `raConnector${active ? " raOk" : ""}`;
                }

                byId("connector-5").className = `raConnector${active && hueActive ? " raOk" : ""}`;

                byId("fpsHint").innerHTML = active
                    ? ""
                    : `Nothing playing on ${deviceLabelWithIp()}. <button type="button" class="raLink" data-open-edit-all="tv">Open TV settings</button>`;
                const enabled = byId("enabled").checked;
                setCardState("pipeline", !enabled ? "off" : "ok", !enabled ? "OFF" : active ? "STREAM" : "READY");
                refreshOverviewCards();
            })
            .catch(() => {});
    }

    // A bare TCP-connect timing to that device's own HTTP(S) port, refreshed
    // every 5 s -- not a real ICMP ping (no raw-socket privilege here), but
    // the same "is it there, how far away does it feel" answer a web
    // dashboard's own ping widget gives. null means "never checked" (no host
    // configured yet): renders nothing, rather than a misleading state.
    // Reachable always carries one decimal; past SlowPingThresholdMs the
    // figure (not the dot) turns amber. Unreachable gets a plain red dot and
    // a red "Unreachable" label instead of a number -- there is nothing to
    // measure once the connect itself failed.
    function pingLine(ping) {
        if (!ping) {
            return "";
        }

        if (!ping.reachable) {
            return `<span class="raDot raDotDead"></span><span class="raDeadLabel">Unreachable</span>`;
        }

        const slow = ping.milliseconds > SlowPingThresholdMs;
        return `<span class="raDot raDotLive"></span><span class="${slow ? "raMsWarn" : "raMsOk"}">${ping.milliseconds.toFixed(1)} ms</span>`;
    }

    // Rack bar + every card's own state/stat/caption -- cheap (string/DOM
    // writes only, nothing fetched here), and it is exactly what makes a
    // setting changed on the "all settings" page below visibly take effect
    // in the cards above without the operator needing to go back to them.
    function refreshOverviewCards() {
        updateRackBar();
        Object.values(sectionSummarisers).forEach(summarise => summarise());
    }

    function pingOne(host, port) {
        if (!host) {
            return Promise.resolve(null);
        }

        return window.ApiClient.getJSON(window.ApiClient.getUrl("RealtimeAmbilight/Discovery/Ping", { host, port }))
            .then(result => ({ reachable: result?.Reachable ?? result?.reachable ?? false, milliseconds: result?.Milliseconds ?? result?.milliseconds ?? null }))
            .catch(() => null);
    }

    // Every 5 s, for the WLED controller and the Hue bridge -- see
    // tvLastSeenLine's own remarks for why the TV is not TCP-pinged the same
    // way, and isAmbilightCalibrated's card has no device of its own to ping.
    function refreshPings() {
        const wledHost = byId("wledEnabled").checked ? (byId("wledHost").value.trim() || loadedConfig?.WledHost || "") : "";
        const hueHost = loadedConfig?.HueBridgeHost || "";
        return Promise.all([
            pingOne(wledHost, Number(byId("wledHttpPort").value) || 80),
            pingOne(hueHost, 443)
        ]).then(([wled, hue]) => {
            latestPings = { wled, hue };
            pingsEverLoaded = true;
            refreshOverviewCards();
            renderWledLiveStatus();
        });
    }

    // The settings page's own record of what this plugin has actually been
    // doing -- see PluginActivityLog server-side. Newest entry at the top,
    // matching how the pill/summary state above it also always shows "now".
    function refreshActivityLog() {
        return window.ApiClient.getJSON(window.ApiClient.getUrl("RealtimeAmbilight/Discovery/Activity"))
            .then(entries => {
                const list = Array.isArray(entries) ? [...entries].reverse() : [];
                show("raActivityEmpty", list.length === 0);
                byId("raActivityList").innerHTML = list.map(entry => {
                    const timestamp = entry.Timestamp ?? entry.timestamp;
                    const level = entry.Level ?? entry.level;
                    const message = entry.Message ?? entry.message;
                    const time = new Date(timestamp).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", second: "2-digit" });
                    const warning = String(level) === "1" || String(level).toLowerCase() === "warning";
                    return `<div class="raActivityRow${warning ? " raActivityWarning" : ""}"><span class="raActivityTime">${time}</span><span class="raActivityMessage">${message}</span></div>`;
                }).join("");
            })
            .catch(() => {});
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
            // Set is="emby-button" via setAttribute on the still-disconnected
            // element, not document.createElement's {is} option -- Jellyfin's
            // own bundled webcomponents polyfill throws on that object form
            // (it expects the older two-string-argument shape), which
            // silently aborted this whole controller's setup before it ever
            // reached view.addEventListener("viewshow", load) further down,
            // leaving the settings page loading forever. Upgrade still
            // happens correctly this way, since the element has not been
            // connected to the document yet when "is" is set.
            const el = document.createElement("button");
            el.setAttribute("is", "emby-button");
            el.type = "button";
            el.className = "raised raStepBtn";
            el.textContent = label;
            el.setAttribute("aria-label", `${label === "−" ? "Decrease" : "Increase"} ${range.getAttribute("label") || "value"}`);
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

    // Mirrors CalibrationWizard.TuningOrder server-side: White, the 20%/40%
    // grey steps, then every RGB primary and secondary. Only used as a
    // fallback bound before the server's own StepCount is known.
    const wizardStepCountFallback = 9;
    let wizardStepIndex = 0;
    let wizardStarted = false;

    function currentTuningPayload() {
        const tuning = {};
        colourTuningFields.forEach(field => { tuning[fieldKey(field)] = Number(byId(field).value); });
        hueAnchorFields.forEach(field => { tuning[field] = hueAnchors[field]; });
        greyBalanceFields.forEach(field => { tuning[field] = greyBalance[field]; });
        return { WallColourHex: byId("wallColourHex").value, Tuning: tuning };
    }

    // White keeps its original mechanic (a plain red/blue gain push-pull,
    // now ±60 instead of ±50 -- 20% more range, on request); each of the six
    // primary/secondary steps instead shows that one colour's own three
    // anchor sliders (hue/brightness/intensity, see hueStepSpecs).
    const wizardWhiteRange = 60;

    // The wizard's 20%/40%-grey steps, right after White: a strip's white
    // balance can drift with brightness, not just at maximum output, so
    // these two get their own temperature (same push-pull mechanic as
    // White's own slider) and tint (green/magenta -- an axis White does not
    // need) correction, independent of White's own flat gain.
    const greyStepLabels = { Grey20: "20% grey", Grey40: "40% grey" };

    function greyControlHtml(colourName, idPrefix) {
        return `<div style="border-left:3px solid currentColor;padding-left:.8em;margin-bottom:1em">
            <strong>${greyStepLabels[colourName]}</strong>
            <div class="fieldDescription">On top of White's own colour temperature, in case this strip's white balance drifts at this brightness.</div>
            <div class="inputContainer">
                <input is="emby-input" id="${idPrefix}Temperature" type="range" min="-60" max="60" step="1" label="Colour temperature" />
                <div class="fieldDescription">Cooler &harr; <strong id="${idPrefix}TemperatureValue"></strong> &harr; Warmer</div>
            </div>
            <div class="inputContainer">
                <input is="emby-input" id="${idPrefix}Tint" type="range" min="-60" max="60" step="1" label="Tint" />
                <div class="fieldDescription">Magenta &harr; <strong id="${idPrefix}TintValue"></strong> &harr; Greener</div>
            </div>
        </div>`;
    }

    function wireGreyControl(colourName, idPrefix) {
        const temperatureField = `${colourName}TemperatureDelta`;
        const tintField = `${colourName}TintDelta`;
        const temperatureInput = byId(`${idPrefix}Temperature`);
        const tintInput = byId(`${idPrefix}Tint`);

        temperatureInput.value = greyBalance[temperatureField];
        tintInput.value = greyBalance[tintField];

        const updateTemperatureLabel = () => {
            const value = Number(temperatureInput.value);
            byId(`${idPrefix}TemperatureValue`).textContent = value === 0 ? "centred" : (value > 0 ? `Warmer (${value})` : `Cooler (${-value})`);
        };
        updateTemperatureLabel();
        temperatureInput.addEventListener("input", () => {
            greyBalance[temperatureField] = Number(temperatureInput.value);
            updateTemperatureLabel();
            retune();
        });

        const updateTintLabel = () => {
            const value = Number(tintInput.value);
            byId(`${idPrefix}TintValue`).textContent = value === 0 ? "centred" : (value > 0 ? `Greener (${value})` : `Magenta (${-value})`);
        };
        updateTintLabel();
        tintInput.addEventListener("input", () => {
            greyBalance[tintField] = Number(tintInput.value);
            updateTintLabel();
            retune();
        });

        addStepButtons(temperatureInput);
        addStepButtons(tintInput);
    }

    function anchorControlHtml(colourName, idPrefix) {
        const spec = hueStepSpecs[colourName];
        return `<div style="border-left:3px solid currentColor;padding-left:.8em;margin-bottom:1em">
            <strong>${colourName}</strong>
            <div class="inputContainer">
                <input is="emby-input" id="${idPrefix}Hue" type="range" min="-23" max="23" step="1" label="Hue" />
                <div class="fieldDescription">${spec.lowLabel} &harr; <strong id="${idPrefix}HueValue"></strong> &harr; ${spec.highLabel}</div>
            </div>
            <div class="inputContainer">
                <input is="emby-input" id="${idPrefix}Brightness" type="range" min="20" max="100" step="1" label="Brightness" />
                <div class="fieldDescription">Now: <strong id="${idPrefix}BrightnessValue"></strong></div>
            </div>
            <div class="inputContainer">
                <input is="emby-input" id="${idPrefix}Intensity" type="range" min="50" max="220" step="1" label="Intensity" />
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

        if (colourName === "White") {
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

        if (greyStepLabels[colourName]) {
            container.innerHTML = greyControlHtml(colourName, "wizardGrey");
            wireGreyControl(colourName, "wizardGrey");
            return;
        }

        if (hueStepSpecs[colourName]) {
            container.innerHTML = anchorControlHtml(colourName, "wizardAnchor");
            wireAnchorControl(colourName, "wizardAnchor");
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

    function describeStatus(state) {
        const active = state.PreviewActive ?? state.previewActive;
        const tvConnected = state.TvConnected ?? state.tvConnected;
        if (!tvConnected) {
            return "Waiting for the TV to open the link above…";
        }
        return active
            ? "Live, adjust the sliders below."
            : "TV connected; waiting for it to load this step's photo…";
    }

    function renderWizardState(state) {
        const stepIndex = state.StepIndex ?? state.stepIndex ?? 0;
        const stepCount = state.StepCount ?? state.stepCount ?? wizardStepCountFallback;
        const colourName = state.ColourName ?? state.colourName ?? "";
        const isLastStep = state.IsLastStep ?? state.isLastStep ?? (stepIndex === stepCount - 1);
        wizardStepIndex = stepIndex;
        const displayName = wizardSwatchDisplayNames[colourName] ?? colourName;
        byId("wizardStepLabel").textContent = `${stepIndex + 1} of ${stepCount}, ${displayName} tuning`;
        const swatch = wizardSwatchColours[colourName];
        if (swatch) {
            const [r, g, b] = swatch;
            const percent = Math.round((Math.max(r, g, b) / 255) * 100);
            byId("wizardSwatchSwab").style.background = `rgb(${r},${g},${b})`;
            byId("wizardSwatchText").textContent = `Sending: RGB(${r}, ${g}, ${b}) — ${displayName} at ${percent}% intensity`;
        }
        renderStepControls(colourName);
        byId("wizardNeutralizedNote").hidden = colourName === "White";
        byId("wizardPrev").disabled = stepIndex === 0;
        // Hidden, not just disabled, on the last step: a disabled-but-still-
        // visible button next to "Stop & release LEDs" read as broken --
        // "Finish" is the only action left at that point.
        byId("wizardNext").hidden = isLastStep;
        byId("finishCalibrationWizard").textContent = isLastStep ? "✓ Done, finish calibration" : "Stop & release LEDs";
        byId("calibrationPreviewStatus").textContent = describeStatus(state);
        if (isLastStep) {
            renderCurveChart();
        }
    }

    function showWizardStarted(started) {
        wizardStarted = started;
        byId("wizardNotStarted").hidden = started;
        byId("wizardActive").hidden = !started;
    }

    // Moves the wizard on the server, which the already-open TV page picks up
    // on its next poll and uploads its own sampled edges from the new step's
    // swatch -- this call never needs to know a swatch's actual colours itself.
    function moveWizard(stepIndex) {
        wizardStepIndex = Math.max(0, Math.min(wizardStepCountFallback - 1, stepIndex));
        return window.ApiClient.ajax({
            type: "POST",
            url: window.ApiClient.getUrl("RealtimeAmbilight/Calibration/WizardState"),
            data: JSON.stringify({
                StepIndex: wizardStepIndex,
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
        byId("samplingDepthValue").textContent = percent === 5 ? "5% (recommended)" : `${percent}%`;
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
        const width = Math.max(16, Number(byId("analysisWidth").value) || (factoryDefaults && factoryDefaults.AnalysisWidth) || 320);
        byId("analysisSizeValue").textContent = `${width} × ${analysisHeight()} pixels`;
    }

    // A configuration written by hand, or by an older build, can hold a width the
    // preset list does not offer. Keep it selectable instead of silently moving
    // the user to a different resolution on the next save.
    function selectAnalysisWidth(width) {
        const select = byId("analysisWidth");
        if (![...select.options].some(option => Number(option.value) === width)) {
            select.add(new Option(`${width} × ${Math.round((width * 9) / 16)}, custom`, String(width)));
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

        const selected = discoveredControllers.find(candidate => candidate.Host === byId("wledCandidates").value);
        const reported = selected?.LedCount ?? 0;
        byId("ledTotalCheck").textContent = reported <= 0 || total === 0
            ? ""
            : total === reported
                ? ", matches the selected controller."
                : `, but the selected controller reports ${reported} LEDs, so these numbers are wrong.`;
        updateLayoutDiagram();
    }

    function analysisHeight() {
        const width = Math.max(16, Number(byId("analysisWidth").value) || (factoryDefaults && factoryDefaults.AnalysisWidth) || 320);
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
        const app = device.app ? ` (${device.app})` : "";
        const when = device.connected ? "connected now" : lastUsedLabel(device.lastUsed);
        return `${device.name}${app} (${when})`;
    }

    function populateDevices(devices, selectedDeviceId) {
        knownDevices = devices;
        const select = byId("targetDeviceId");
        select.textContent = "";
        select.add(new Option("All devices, not bound", ""));
        devices.forEach(device => {
            const option = new Option(deviceLabel(device), device.id);
            option.dataset.deviceName = device.name;
            select.add(option);
        });

        if (selectedDeviceId && !devices.some(device => device.id === selectedDeviceId)) {
            // Keep a binding to a device Jellyfin does not currently list visible
            // and intact, instead of silently resetting it to "all devices" on
            // save -- named after the device itself (already known from its own
            // saved name), not a generic "unknown device" that reads as broken
            // for a TV that is still used every day, just not in /Devices or
            // /Sessions at this exact moment.
            const savedName = loadedConfig?.TargetDeviceName || "Saved device";
            const saved = new Option(`${savedName} (not currently connected)`, selectedDeviceId);
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
            select.add(new Option(`${candidate.Name} (${candidate.Host})${leds}${version}`, candidate.Host));
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
            show("wledOffsetWarning", false);
            show("wledNightlightWarning", false);
            return Promise.resolve();
        }

        return window.ApiClient
            .getJSON(window.ApiClient.getUrl("RealtimeAmbilight/Discovery/Settings", connection))
            .then(settings => {
                show("maxBrightnessWarning", Boolean(settings) && !settings.ForcesMaxBrightness);
                const maxPower = settings?.MaxPowerMilliamps ?? settings?.maxPowerMilliamps;
                abl.textContent = maxPower > 0
                    ? `Power limit (ABL) on WLED: ${maxPower} mA, read-only, this plugin never changes it.`
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
                        ? `WLED's own refresh cap: ${ledFps} fps, lower than the ${outputFps} fps this plugin is sending; raising "LED updates per second" further will not help.`
                        : `WLED's own refresh cap: ${ledFps} fps.`
                    : "";

                const timeoutMs = settings?.RealtimeTimeoutMilliseconds ?? settings?.realtimeTimeoutMilliseconds;
                timeoutStatus.textContent = timeoutMs
                    ? `WLED reclaims the strip after ${(timeoutMs / 1000).toFixed(1)} s without new data.`
                    : "";

                const rgbwMisconfigured = Boolean(settings && (settings.RgbwModeIsMisconfigured ?? settings.rgbwModeIsMisconfigured));
                show("wledRgbwModeWarning", rgbwMisconfigured);

                const pixelOffset = settings?.RealtimePixelOffset ?? settings?.realtimePixelOffset ?? 0;
                show("wledOffsetWarning", pixelOffset !== 0);

                const nightlightInterferes = Boolean(settings && (settings.NightlightInterferesWithOutput ?? settings.nightlightInterferesWithOutput));
                show("wledNightlightWarning", nightlightInterferes);
            })
            .catch(() => {
                show("maxBrightnessWarning", false);
                show("rgbwSuggestion", false);
                show("wledRgbwModeWarning", false);
                show("wledOffsetWarning", false);
                show("wledNightlightWarning", false);
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

    function fixRealtimePixelOffset() {
        const connection = currentWledConnection();
        const status = byId("fixRealtimePixelOffsetStatus");
        if (!connection) {
            return Promise.resolve();
        }

        byId("fixRealtimePixelOffset").disabled = true;
        status.textContent = "Resetting to 0…";
        return window.ApiClient.ajax({
            type: "POST",
            url: window.ApiClient.getUrl("RealtimeAmbilight/Discovery/ResetRealtimePixelOffset", connection)
        }).then(() => {
            status.textContent = "Done. Checking WLED again…";
            return checkControllerSettings();
        }).then(() => {
            status.textContent = byId("wledOffsetWarning").style.display !== "none"
                ? "WLED still reports a nonzero offset; you may need to change it in WLED directly."
                : "Fixed.";
        }).catch(error => {
            status.textContent = error?.status === 403
                ? "Turn on \"Allow this plugin to fix WLED settings\" above, save, and try again."
                : (error?.responseText || "WLED did not accept the change.");
        }).finally(() => { byId("fixRealtimePixelOffset").disabled = false; });
    }

    function fixForceMaxBrightness() {
        const connection = currentWledConnection();
        const status = byId("fixForceMaxBrightnessStatus");
        if (!connection) {
            return Promise.resolve();
        }

        byId("fixForceMaxBrightness").disabled = true;
        status.textContent = "Turning it on…";
        return window.ApiClient.ajax({
            type: "POST",
            url: window.ApiClient.getUrl("RealtimeAmbilight/Discovery/FixForceMaxBrightness", connection)
        }).then(() => {
            status.textContent = "Done. Checking WLED again…";
            return checkControllerSettings();
        }).then(() => {
            if (byId("maxBrightnessWarning").style.display !== "none") {
                status.textContent = "WLED still reports it as off; you may need to change it in WLED directly.";
            } else {
                status.textContent = "Fixed.";
            }
        }).catch(error => {
            status.textContent = error?.status === 403
                ? "Turn on \"Allow this plugin to fix WLED settings\" above, save, and try again."
                : (error?.responseText || "WLED did not accept the change.");
        }).finally(() => { byId("fixForceMaxBrightness").disabled = false; });
    }

    // Same dot-plus-ping language as the overview card, not a plain text
    // line -- and combines both signals this modal already has: WLED's own
    // reported online/realtime state (from checkControllerStatus, below)
    // and the TCP round trip (from refreshPings, already polling every 5 s).
    function renderWledLiveStatus() {
        const el = byId("wledLiveStatus");
        if (!el) {
            return;
        }

        if (!currentWledConnection()) {
            el.innerHTML = `<span class="raDot raDotDead"></span><span class="raDeadLabel">Choose or enter a WLED address.</span>`;
            return;
        }

        const online = latestWledStatus?.IsOnline ?? latestWledStatus?.isOnline;
        const realtime = latestWledStatus?.IsRealtimeActive ?? latestWledStatus?.isRealtimeActive;
        if (!online) {
            el.innerHTML = `<span class="raDot raDotDead"></span><span class="raDeadLabel">Not reachable. Check address, port and network.</span>`;
            return;
        }

        const ping = latestPings.wled;
        const pingText = ping?.reachable
            ? ` · <span class="${ping.milliseconds > SlowPingThresholdMs ? "raMsWarn" : "raMsOk"}">${ping.milliseconds.toFixed(1)} ms</span>`
            : "";
        el.innerHTML = `<span class="raDot raDotLive"></span>${realtime ? "Realtime Ambilight active" : "WLED is ready"}${pingText}`;
    }

    function checkControllerStatus() {
        if (!byId("wledEnabled").checked) return Promise.resolve();
        const connection = currentWledConnection();
        if (!connection) {
            latestWledStatus = null;
            renderWledLiveStatus();
            return Promise.resolve();
        }

        return window.ApiClient
            .getJSON(window.ApiClient.getUrl("RealtimeAmbilight/Discovery/Status", connection))
            .then(result => {
                latestWledStatus = result;
                renderWledLiveStatus();
                refreshOverviewCards();
            })
            .catch(() => {
                latestWledStatus = null;
                renderWledLiveStatus();
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

    // Fetched once and cached: every page load after the first reuses the
    // same factory-default snapshot rather than re-requesting it.
    let factoryDefaultsPromise = null;
    function ensureFactoryDefaults() {
        if (!factoryDefaultsPromise) {
            factoryDefaultsPromise = window.ApiClient
                .getJSON(window.ApiClient.getUrl("RealtimeAmbilight/Discovery/DefaultConfiguration"))
                .then(config => { factoryDefaults = config; return config; });
        }
        return factoryDefaultsPromise;
    }

    function load() {
        Dashboard.showLoadingMsg();
        return Promise.all([window.ApiClient.getPluginConfiguration(pluginId), ensureFactoryDefaults()])
            .then(([config]) => {
                loadedConfig = config;
                resetHueAnchors(config);
                resetGreyBalance(config);
                byId("enabled").checked = config.Enabled !== false;
                byId("wledEnabled").checked = config.WledEnabled !== false;
                byId("disableAmbilightDuringDaytime").checked = config.DisableAmbilightDuringDaytime === true;
                byId("holdWhilePaused").checked = config.HoldWhilePaused !== false;
                byId("ignoreBlackBorders").checked = config.IgnoreBlackBorders !== false;
                byId("correctLedGamma").checked = config.CorrectLedGamma !== false;
                byId("autoDetectLedGamma").checked = config.AutoDetectLedGamma !== false;
                byId("allowWledControl").checked = config.AllowWledControl === true;
                byId("sendWhiteChannel").checked = config.SendWhiteChannel === true;
                byId("reverseTopLeds").checked = config.ReverseTopLeds === true;
                byId("reverseRightLeds").checked = config.ReverseRightLeds === true;
                byId("reverseBottomLeds").checked = config.ReverseBottomLeds === true;
                byId("reverseLeftLeds").checked = config.ReverseLeftLeds === true;
                byId("whiteChannelStrengthPercent").value = config.WhiteChannelStrengthPercent ?? 50;
                byId("hueEnabled").checked = config.HueEnabled === true;
                byId("hueBrightnessPercent").value = config.HueBrightnessPercent || 100;
                byId("hueResponsePercent").value = config.HueResponsePercent ?? 40;
                byId("hueBlackLevelFloorPercent").value = config.HueBlackLevelFloorPercent ?? 1;
                byId("hueScreenWidthPercent").value = config.HueScreenWidthPercent ?? 100;
                byId("hueScreenHeightPercent").value = config.HueScreenHeightPercent ?? 100;
                byId("hueScreenCentreHeightPercent").value = config.HueScreenCentreHeightPercent ?? 0;
                byId("hueScreenCentreWidthPercent").value = config.HueScreenCentreWidthPercent ?? 0;
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
                        ? ({ Auto: 0, HyperionRawRgb: 1, Ddp: 2 }[config[key]] ?? config[key] ?? factoryDefaults?.[key])
                        : config[key] ?? factoryDefaults?.[key];
                    byId(field).value = value;
                });
                byId("wledHost").value = config.WledHost || "";
                const wallColourHex = /^#[0-9a-f]{6}$/i.test(config.WallColourHex || "")
                    ? config.WallColourHex.toLowerCase()
                    : "#ffffff";
                byId("wallColourHex").value = wallColourHex;
                syncWallColourPresetFromHex(wallColourHex);
                selectAnalysisWidth(Math.max(16, Number(config.AnalysisWidth) || (factoryDefaults && factoryDefaults.AnalysisWidth) || 320));
                view.querySelectorAll('input[type="range"]').forEach(range => range.dispatchEvent(new Event("input")));
                setDelayLabel();
                setAnalysisLabel();
                setDepthLabel();
                setMinimumColourHoldLabel();
                setWledSmoothingLabel();
                setColourLabels();
                updateCalibrationPatternUrl();
                setLedTotal();

                // Everything below reads only what was just set above (or its
                // own already-configured values), so none of it actually
                // depends on any of the others finishing first -- there is no
                // real reason for this to have ever been one long chain.
                // Once a WLED controller is already configured, the ~1.5 s
                // mDNS scan is skipped entirely on every page load (it stays
                // one click away via "Scan the network for WLED"); this alone
                // was the single biggest cost on a normal, already-set-up load.
                const alreadyConfigured = Boolean(config.WledHost);
                if (alreadyConfigured) {
                    show("wledCandidatesContainer", false);
                    show("manualWledContainer", true);
                }

                return Promise.all([
                    loadDevices(config.TargetDeviceId || ""),
                    !byId("wledEnabled").checked || alreadyConfigured ? Promise.resolve() : findWled(),
                    byId("wledEnabled").checked ? checkControllerSettings() : Promise.resolve(),
                    byId("wledEnabled").checked ? checkControllerStatus() : Promise.resolve(),
                    loadWizardState(),
                    // On a normal page load (not right after an interactive
                    // pairing), nothing else ever populates the entertainment-area
                    // dropdown -- do it here too whenever a bridge is already
                    // paired, or the saved selection has nothing to bind to.
                    loadHueStatus().then(paired => paired ? refreshHueEntertainmentConfigs() : null)
                ]);
            })
            .then(() => {
                refreshOverviewCards();
                refreshPings();
                refreshActivityLog();
                closeEditModal();
            })
            .finally(() => Dashboard.hideLoadingMsg());
    }

    function save(event) {
        event.preventDefault();
        const usingFinder = byId("wledCandidatesContainer").style.display !== "none";
        const host = (usingFinder ? byId("wledCandidates").value : byId("wledHost").value.trim());
        if (!host && byId("wledEnabled").checked) {
            Dashboard.alert("Enter a WLED address first, or pick a discovered controller.");
            return;
        }

        Dashboard.showLoadingMsg();
        const [hostName, hostPort] = host.split(":");
        const config = {
            ...loadedConfig,
            ConfigSchemaVersion: 3,
            Enabled: byId("enabled").checked,
            WledEnabled: byId("wledEnabled").checked,
            DisableAmbilightDuringDaytime: byId("disableAmbilightDuringDaytime").checked,
            HoldWhilePaused: byId("holdWhilePaused").checked,
            IgnoreBlackBorders: byId("ignoreBlackBorders").checked,
            ReverseTopLeds: byId("reverseTopLeds").checked,
            ReverseRightLeds: byId("reverseRightLeds").checked,
            ReverseBottomLeds: byId("reverseBottomLeds").checked,
            ReverseLeftLeds: byId("reverseLeftLeds").checked,
            CorrectLedGamma: byId("correctLedGamma").checked,
            AutoDetectLedGamma: byId("autoDetectLedGamma").checked,
            AllowWledControl: byId("allowWledControl").checked,
            SendWhiteChannel: byId("sendWhiteChannel").checked,
            WhiteChannelStrengthPercent: Number(byId("whiteChannelStrengthPercent").value),
            TargetDeviceId: byId("targetDeviceId").value,
            TargetDeviceName: targetDeviceName(),
            WledHost: hostName || loadedConfig?.WledHost || "",
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
            HueBlackLevelFloorPercent: Number(byId("hueBlackLevelFloorPercent").value),
            HueScreenWidthPercent: Number(byId("hueScreenWidthPercent").value),
            HueScreenHeightPercent: Number(byId("hueScreenHeightPercent").value),
            HueScreenCentreHeightPercent: Number(byId("hueScreenCentreHeightPercent").value),
            HueScreenCentreWidthPercent: Number(byId("hueScreenCentreWidthPercent").value),
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
                closeEditModal();
                return Dashboard.processPluginConfigurationUpdateResult(result);
            })
            .finally(() => Dashboard.hideLoadingMsg());
    }

    // The Live tuning panel's own save path: deliberately not save() above.
    // save() rebuilds and validates the *whole* form (it refuses without a
    // WLED address, for one), which is the wrong failure mode for a slider an
    // operator is dragging while watching the TV, not the screen. This only
    // ever merges the tuning fields into whatever was last loaded, so it can
    // never clobber an in-progress, not-yet-saved edit sitting open in a
    // connection modal elsewhere on the page.
    const tuningFields = ["brightnessPercent", "saturationPercent", "blackLevelFloorPercent", "whiteChannelStrengthPercent", "samplingDepthPercent", "wledSmoothingMilliseconds", "minimumColourHoldMilliseconds", "hueBrightnessPercent", "hueResponsePercent", "hueBlackLevelFloorPercent", "hueScreenWidthPercent", "hueScreenHeightPercent", "hueScreenCentreHeightPercent", "hueScreenCentreWidthPercent"];
    let tuningSaveTimer = null;

    function scheduleTuningSave() {
        byId("tuningSavedIndicator").textContent = "Applying…";
        byId("tuningSavedIndicator").className = "raCardState";
        if (tuningSaveTimer) {
            clearTimeout(tuningSaveTimer);
        }

        tuningSaveTimer = setTimeout(saveTuningFieldsNow, 700);
    }

    function saveTuningFieldsNow() {
        tuningSaveTimer = null;
        if (!loadedConfig) {
            return;
        }

        const config = { ...loadedConfig };
        tuningFields.forEach(field => { config[fieldKey(field)] = Number(byId(field).value); });
        window.ApiClient.updatePluginConfiguration(pluginId, config)
            .then(() => {
                loadedConfig = config;
                byId("tuningSavedIndicator").textContent = "Saved";
                byId("tuningSavedIndicator").className = "raCardState raOk";
                setTimeout(() => {
                    if (!tuningSaveTimer) {
                        byId("tuningSavedIndicator").textContent = "";
                    }
                }, 1500);
            })
            .catch(() => {
                byId("tuningSavedIndicator").textContent = "Could not save";
                byId("tuningSavedIndicator").className = "raCardState raWarn";
            });
    }

    // --- Per-section "Reset to defaults" -----------------------------------
    // Each section of the page gets its own reset button instead of one
    // global one, so resetting a wrong LED count no longer means also
    // wiping colour calibration tuned separately, and vice versa.
    //
    // factoryDefaults (fetched once from RealtimeAmbilight/Discovery/DefaultConfiguration,
    // i.e. PluginConfiguration's own C# property initialisers) already
    // carries every field a reset needs, so there is no second,
    // hand-maintained copy of those numbers here to drift out of sync with
    // the real defaults -- exactly what happened to this file's own former
    // hardcoded defaults object. The Hue bridge pairing itself (host/id/
    // entertainment area) is always carried forward from loadedConfig
    // instead of reset: unlinking that bridge is a separate, explicit
    // action ("Unlink this bridge" on the Hue tab), never implied by
    // resetting preferences.
    const tvResetFields = ["Enabled", "IgnoreBlackBorders", "AnalysisWidth", "AnalysisHeight", "TargetDeviceId", "TargetDeviceName"];
    const wledResetFields = [
        "WledHost", "WledHttpPort", "RealtimeProtocol", "TopLedCount", "RightLedCount", "BottomLedCount", "LeftLedCount",
        "OutputDelayMilliseconds", "OutputFramesPerSecond", "HoldWhilePaused", "SendWhiteChannel", "AllowWledControl",
        "AutoDetectLedGamma", "CorrectLedGamma",
        "ReverseTopLeds", "ReverseRightLeds", "ReverseBottomLeds", "ReverseLeftLeds"
    ];
    const ambilightResetFields = [
        "RedGainPercent", "GreenGainPercent", "BlueGainPercent", "WallColourHex", "WallColourCorrectionPercent",
        ...hueAnchorFields, ...greyBalanceFields, ...sideTuningFields
    ];
    const hueResetFields = ["HueEnabled", "HueEndBehaviour", "HueScreenWidthPercent", "HueScreenHeightPercent", "HueScreenCentreHeightPercent", "HueScreenCentreWidthPercent"];

    function resetFields(fieldNames, sectionLabel) {
        if (!factoryDefaults) {
            return;
        }

        const confirmed = window.confirm(`Reset ${sectionLabel} to its factory defaults? Saves immediately.`);
        if (!confirmed) {
            return;
        }

        Dashboard.showLoadingMsg();
        const config = {
            ...loadedConfig,
            ...Object.fromEntries(fieldNames.map(name => [name, factoryDefaults[name]])),
            HueBridgeHost: loadedConfig?.HueBridgeHost || "",
            HueBridgeId: loadedConfig?.HueBridgeId || "",
            HueEntertainmentConfigurationId: loadedConfig?.HueEntertainmentConfigurationId || "00000000-0000-0000-0000-000000000000",
            HueEntertainmentConfigurationName: loadedConfig?.HueEntertainmentConfigurationName || ""
        };
        window.ApiClient.updatePluginConfiguration(pluginId, config)
            .then(result => {
                loadedConfig = config;
                return Dashboard.processPluginConfigurationUpdateResult(result);
            })
            .then(load)
            .finally(() => Dashboard.hideLoadingMsg());
    }

    function resetTvSection() { resetFields(tvResetFields, "TV"); }
    function resetWledSection() { resetFields(wledResetFields, "WLED"); }
    function resetAmbilightSection() { resetFields(ambilightResetFields, "colour calibration"); }
    function resetHueSection() { resetFields(hueResetFields, "Hue"); }
    function resetTuningSection() { resetFields(tuningFields.map(fieldKey), "Live tuning"); }

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
            if (status && Number(status.ChannelCount) > 0 && byId("hueIdentifyButtons").childElementCount === 0) {
                renderHueIdentifyButtons(Number(status.ChannelCount));
            }
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
                    select.add(new Option(`${host} (${bridgeId})`, host));
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
            ? "Balanced (50 of 100)"
            : value < 50 ? `Reactive (${value} of 100)` : `Smooth (${value} of 100)`;
    }

    populateWallColourPresets();
    createSideTuningCards();
    addRangeScales();

    addStepButtonsToSection();
    // Delegated, not a per-element listener: some [data-open-edit-all]
    // elements (e.g. the "Open TV settings" link inside the FPS chain hint)
    // are (re)created after this point by innerHTML writes, so binding once
    // at init would silently miss them.
    view.addEventListener("click", event => {
        const openEdit = event.target.closest("[data-open-edit-all]");
        if (openEdit) {
            openEditModal(openEdit.dataset.openEditAll);
            return;
        }

        if (event.target.closest("[data-close-modal]") || event.target.id === "raModalBackdrop") {
            closeEditModal();
        }
    });
    view.addEventListener("keydown", event => {
        if (event.key === "Escape" && openModalSection) {
            closeEditModal();
        }
    });
    view.addEventListener("input", event => { if (event.target.closest(".raModalBody")) { updateModalScrollFade(); } });
    view.addEventListener("change", event => { if (event.target.closest(".raModalBody")) { updateModalScrollFade(); } });
    window.addEventListener("resize", updateModalScrollFade);

    tuningFields.forEach(field => byId(field).addEventListener("input", scheduleTuningSave));

    setInterval(refreshFpsChain, 1000);
    // Every 5 s: a real network round trip to WLED/Hue (status + ping), not
    // just a cheap in-process counter read like refreshFpsChain -- but still
    // often enough for the cards to feel genuinely live.
    setInterval(() => { if (byId("wledEnabled").checked) checkControllerStatus(); loadHueStatus().then(refreshOverviewCards); refreshPings(); }, 5000);
    setInterval(refreshActivityLog, 5000);
    view.addEventListener("viewshow", load);
    byId("realtimeAmbilightConfigurationForm").addEventListener("submit", save);
    byId("resetTvSection").addEventListener("click", resetTvSection);
    byId("resetWledSection").addEventListener("click", resetWledSection);
    byId("resetAmbilightSection").addEventListener("click", resetAmbilightSection);
    byId("resetHueSection").addEventListener("click", resetHueSection);
    byId("resetTuningSection").addEventListener("click", resetTuningSection);
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
    byId("wledEnabled").addEventListener("change", () => {
        if (byId("wledEnabled").checked) checkControllerStatus();
        refreshPings();
    });
    byId("wledHost").addEventListener("change", () => { checkControllerSettings(); checkControllerStatus(); });
    byId("wledHttpPort").addEventListener("change", () => { checkControllerSettings(); checkControllerStatus(); });
    byId("startCalibrationWizard").addEventListener("click", startWizard);
    byId("testVideosToggle").addEventListener("click", toggleTestVideosPanel);
    byId("livePreviewToggle").addEventListener("click", toggleLivePreviewPanel);
    byId("wizardPrev").addEventListener("click", () => moveWizard(wizardStepIndex - 1));
    byId("wizardNext").addEventListener("click", () => moveWizard(wizardStepIndex + 1));
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
    byId("whiteChannelStrengthPercent").addEventListener("input", setColourLabels);
    byId("sendWhiteChannel").addEventListener("change", setColourLabels);
    byId("allowWledControl").addEventListener("change", () => {
        byId("allowWledControlSummary").textContent = byId("allowWledControl").checked
            ? "This plugin may fix WLED settings for you. Save to keep it that way."
            : "This plugin only reads WLED until you turn this on.";
    });
    byId("hueEnabled").addEventListener("change", () => {
        byId("hueSelectionSection").classList.toggle("raDimmed", !byId("hueEnabled").checked);
    });
    byId("fixForceMaxBrightness").addEventListener("click", fixForceMaxBrightness);
    byId("fixRgbwMode").addEventListener("click", fixRgbwMode);
    byId("fixRealtimePixelOffset").addEventListener("click", fixRealtimePixelOffset);
    byId("hueScanBridges").addEventListener("click", scanHueBridges);
    byId("hueStartPairing").addEventListener("click", startHuePairing);
    byId("hueRefreshConfigs").addEventListener("click", refreshHueEntertainmentConfigs);
    byId("hueUnlink").addEventListener("click", unlinkHue);
    byId("hueBrightnessPercent").addEventListener("input", setHueBrightnessLabel);
    byId("hueResponsePercent").addEventListener("input", setHueResponseLabel);
    byId("hueBlackLevelFloorPercent").addEventListener("input", setColourLabels);
}
