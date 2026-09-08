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
        "topLedCount", "rightLedCount", "bottomLedCount", "leftLedCount", "analysisFramesPerSecond", "samplingDepthPercent", ...colourTuningFields
    ];
    const defaults = {
        WledHttpPort: 80, RealtimeProtocol: 0, OutputDelayMilliseconds: 0, OutputFramesPerSecond: 30,
        StopFadeMilliseconds: 250, TopLedCount: 265, RightLedCount: 150,
        BottomLedCount: 266, LeftLedCount: 150, AnalysisWidth: 160, AnalysisFramesPerSecond: 30,
        SamplingDepthPercent: 10, BrightnessPercent: 100, SaturationPercent: 100,
        RedGainPercent: 100, GreenGainPercent: 100, BlueGainPercent: 100,
        BlackLevelFloorPercent: 0,
        WallColourCorrectionPercent: 100,
        TopBrightnessPercent: 100, TopRedGainPercent: 100, TopGreenGainPercent: 100, TopBlueGainPercent: 100,
        RightBrightnessPercent: 100, RightRedGainPercent: 100, RightGreenGainPercent: 100, RightBlueGainPercent: 100,
        BottomBrightnessPercent: 100, BottomRedGainPercent: 100, BottomGreenGainPercent: 100, BottomBlueGainPercent: 100,
        LeftBrightnessPercent: 100, LeftRedGainPercent: 100, LeftGreenGainPercent: 100, LeftBlueGainPercent: 100
    };
    const wallColourPresets = [
        { label: "White — no correction", hex: "#ffffff" },
        { label: "Off-white", hex: "#efe9df" },
        { label: "Light grey", hex: "#b7b6b2" },
        { label: "Warm grey (greige)", hex: "#a89f8f" },
        { label: "Anthracite grey", hex: "#4b4c4c" },
        { label: "Charcoal / almost black", hex: "#2b2b2b" },
        { label: "Sand / beige", hex: "#d8c9a8" },
        { label: "Taupe", hex: "#8a7866" },
        { label: "Sage green", hex: "#8a9a83" },
        { label: "Hunter / forest green", hex: "#33422f" },
        { label: "Navy blue", hex: "#1f2c44" },
        { label: "Terracotta", hex: "#b1583a" }
    ];
    const ledCountFields = ["topLedCount", "rightLedCount", "bottomLedCount", "leftLedCount"];
    let loadedConfig = null;
    let discoveredControllers = [];
    let knownDevices = [];
    let calibrationPreviewIsActive = false;
    let calibrationPreviewTimer = null;
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

            const suffix = range.id.includes("Percent") ? "%" : range.id === "outputDelayMilliseconds" ? " ms" : "";
            const update = () => {
                scale.innerHTML = `<span>${range.min}${suffix}</span><strong>${range.value}${suffix}</strong><span>${range.max}${suffix}</span>`;
            };
            range.addEventListener("input", update);
            update();
        });
    }

    // One fixed link now: the TV page polls its own state instead of being
    // re-opened with new query parameters for every step.
    function updateCalibrationPatternUrl() {
        const url = new URL(window.ApiClient.getUrl("RealtimeAmbilight/Calibration/Pattern"), window.location.origin).href;
        byId("calibrationPatternUrl").value = url;
        byId("openCalibrationPattern").href = url;
    }

    const wizardColours = ["White", "Blue", "Red", "Green", "Yellow", "Purple", "Orange"];
    let wizardStepIndex = 0;
    let wizardPhotoIndex = 0;
    let wizardPhotoCount = 0;

    function currentTuningPayload() {
        const tuning = {};
        colourTuningFields.forEach(field => { tuning[fieldKey(field)] = Number(byId(field).value); });
        return { WallColourHex: byId("wallColourHex").value, Tuning: tuning };
    }

    function calibrationRequest() {
        return {
            Side: byId("calibrationSide").value,
            Colour: wizardColours[wizardStepIndex],
            ...currentTuningPayload()
        };
    }

    function renderWizardState(state) {
        const stepIndex = state.StepIndex ?? state.stepIndex ?? 0;
        const stepCount = state.StepCount ?? state.stepCount ?? wizardColours.length;
        const colourName = state.ColourName ?? state.colourName ?? wizardColours[stepIndex];
        const photoCount = state.PhotoCount ?? state.photoCount ?? 0;
        wizardStepIndex = stepIndex;
        wizardPhotoIndex = state.PhotoIndex ?? state.photoIndex ?? 0;
        wizardPhotoCount = photoCount;
        byId("wizardStepLabel").textContent = `${stepIndex + 1} of ${stepCount} — ${colourName} tuning`;
        byId("wizardPrev").disabled = stepIndex === 0;
        byId("wizardNext").disabled = stepIndex === stepCount - 1;
        byId("wizardAnotherPhoto").disabled = photoCount <= 1;
    }

    // Moves the wizard on the server, which the open TV page picks up on its
    // next poll, and -- only if a preview is already running -- carries the
    // sliders' current values along so the LEDs are restarted in sync.
    function moveWizard(stepIndex, photoIndex) {
        wizardStepIndex = Math.max(0, Math.min(wizardColours.length - 1, stepIndex));
        wizardPhotoIndex = Math.max(0, photoIndex);
        return window.ApiClient.ajax({
            type: "POST",
            url: window.ApiClient.getUrl("RealtimeAmbilight/Calibration/WizardState"),
            data: JSON.stringify({
                StepIndex: wizardStepIndex,
                PhotoIndex: wizardPhotoIndex,
                Side: byId("calibrationSide").value,
                ...currentTuningPayload()
            }),
            contentType: "application/json",
            dataType: "json"
        }).then(renderWizardState);
    }

    function loadWizardState() {
        return window.ApiClient
            .getJSON(window.ApiClient.getUrl("RealtimeAmbilight/Calibration/WizardState"))
            .then(state => {
                byId("calibrationSide").value = state.Side ?? state.side ?? "Top";
                renderWizardState(state);
            })
            .catch(() => {});
    }

    function startCalibrationPreview(quietly = false) {
        const status = byId("calibrationPreviewStatus");
        if (!quietly) {
            status.textContent = "Starting the selected LED side…";
        }
        return window.ApiClient.ajax({
            type: "POST",
            url: window.ApiClient.getUrl("RealtimeAmbilight/Calibration/Preview"),
            data: JSON.stringify(calibrationRequest()),
            contentType: "application/json",
            dataType: "json"
        }).then(response => {
            calibrationPreviewIsActive = true;
            status.textContent = response.Message || response.message || "Preview is live. Adjust the sliders until the two colours meet.";
        }).catch(error => {
            calibrationPreviewIsActive = false;
            status.textContent = error?.responseJSON?.Message || "The preview could not start. Stop Jellyfin playback and try again.";
        });
    }

    function refreshLiveCalibrationPreview() {
        setColourLabels();
        if (!calibrationPreviewIsActive) {
            return;
        }
        clearTimeout(calibrationPreviewTimer);
        calibrationPreviewTimer = setTimeout(() => startCalibrationPreview(true), 100);
    }

    function stopCalibrationPreview() {
        clearTimeout(calibrationPreviewTimer);
        return window.ApiClient.ajax({
            type: "DELETE",
            url: window.ApiClient.getUrl("RealtimeAmbilight/Calibration/Preview")
        }).then(() => {
            calibrationPreviewIsActive = false;
            byId("calibrationPreviewStatus").textContent = "Preview stopped; WLED will take back control in its normal timeout.";
        }).catch(() => {
            byId("calibrationPreviewStatus").textContent = "The stop request did not complete; the preview will release on WLED's normal timeout.";
        });
    }

    function setDepthLabel() {
        const percent = Number(byId("samplingDepthPercent").value);
        byId("samplingDepthValue").textContent = percent === 10 ? "10% (recommended)" : `${percent}%`;
        updateLayoutDiagram();
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
            })
            .catch(() => {
                show("maxBrightnessWarning", false);
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
                setColourLabels();
                updateCalibrationPatternUrl();
                setLedTotal();
                return loadDevices(config.TargetDeviceId || "");
            })
            .then(findWled)
            .then(() => Promise.all([checkControllerSettings(), checkControllerStatus(), loadWizardState()]))
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
            TargetDeviceId: byId("targetDeviceId").value,
            TargetDeviceName: targetDeviceName(),
            WledHost: hostName,
            AnalysisWidth: Number(byId("analysisWidth").value),
            AnalysisHeight: analysisHeight(),
            WallColourHex: byId("wallColourHex").value
        };
        numericFields.forEach(field => { config[fieldKey(field)] = Number(byId(field).value); });
        if (hostPort) {
            config.WledHttpPort = Number(hostPort);
        }

        window.ApiClient.updatePluginConfiguration(pluginId, config)
            .then(Dashboard.processPluginConfigurationUpdateResult)
            .finally(() => Dashboard.hideLoadingMsg());
    }

    populateWallColourPresets();
    createSideTuningCards();
    addRangeScales();
    restoreLastTab();
    view.querySelectorAll(".raTab").forEach(button => button.addEventListener("click", () => switchTab(button.dataset.tab)));
    view.addEventListener("viewshow", load);
    byId("realtimeAmbilightConfigurationForm").addEventListener("submit", save);
    byId("outputDelayMilliseconds").addEventListener("input", setDelayLabel);
    byId("samplingDepthPercent").addEventListener("input", setDepthLabel);
    colourTuningFields.forEach(field => byId(field).addEventListener("input", refreshLiveCalibrationPreview));
    byId("wallColourHex").addEventListener("input", refreshLiveCalibrationPreview);
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
    byId("calibrationSide").addEventListener("change", () => {
        moveWizard(wizardStepIndex, wizardPhotoIndex);
        if (calibrationPreviewIsActive) {
            startCalibrationPreview(true);
        }
    });
    byId("wizardPrev").addEventListener("click", () => {
        moveWizard(wizardStepIndex - 1, 0).then(() => {
            if (calibrationPreviewIsActive) {
                startCalibrationPreview(true);
            }
        });
    });
    byId("wizardNext").addEventListener("click", () => {
        moveWizard(wizardStepIndex + 1, 0).then(() => {
            if (calibrationPreviewIsActive) {
                startCalibrationPreview(true);
            }
        });
    });
    byId("wizardAnotherPhoto").addEventListener("click", () => {
        moveWizard(wizardStepIndex, wizardPhotoIndex + 1);
    });
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
    byId("startCalibrationPreview").addEventListener("click", () => startCalibrationPreview());
    byId("stopCalibrationPreview").addEventListener("click", stopCalibrationPreview);
    byId("wallColourPreset").addEventListener("change", () => {
        const value = byId("wallColourPreset").value;
        if (value === "custom") {
            show("wallColourCustomContainer", true);
        } else {
            show("wallColourCustomContainer", false);
            byId("wallColourHex").value = value;
        }
        refreshLiveCalibrationPreview();
    });
    byId("allowWledControl").addEventListener("change", () => {
        byId("allowWledControlSummary").textContent = byId("allowWledControl").checked
            ? "This plugin may fix WLED settings for you. Save to keep it that way."
            : "This plugin only reads WLED until you turn this on.";
    });
    byId("fixForceMaxBrightness").addEventListener("click", fixForceMaxBrightness);
}
