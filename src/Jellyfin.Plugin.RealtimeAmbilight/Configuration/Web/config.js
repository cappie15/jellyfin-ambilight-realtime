export default function (view) {
    const pluginId = "7d6d91ed-0f36-46ea-9868-9623283b6b51";
    const numericFields = [
        "wledHttpPort", "realtimeProtocol", "outputDelayMilliseconds", "outputFramesPerSecond", "pauseKeepAliveSeconds", "stopFadeMilliseconds",
        "topLedCount", "rightLedCount", "bottomLedCount", "leftLedCount", "analysisWidth", "analysisFramesPerSecond"
    ];
    const defaults = {
        WledHttpPort: 80, RealtimeProtocol: 0, OutputDelayMilliseconds: 0, OutputFramesPerSecond: 30,
        PauseKeepAliveSeconds: 2, StopFadeMilliseconds: 250, TopLedCount: 265, RightLedCount: 150,
        BottomLedCount: 266, LeftLedCount: 150, AnalysisWidth: 160, AnalysisFramesPerSecond: 30
    };
    let loadedConfig = null;
    const byId = id => view.querySelector(`#${id}`);
    const fieldKey = field => field[0].toUpperCase() + field.slice(1);
    const show = (id, visible) => { byId(id).style.display = visible ? "block" : "none"; };

    function setDelayLabel() {
        const value = Number(byId("outputDelayMilliseconds").value);
        byId("outputDelayValue").textContent = value === 0 ? "0 ms (no delay)" : `${value} ms`;
    }

    function setAnalysisLabel() {
        const width = Math.max(16, Number(byId("analysisWidth").value) || defaults.AnalysisWidth);
        byId("analysisSizeValue").textContent = `${width} × ${analysisHeight()} pixels`;
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

    function deviceLabel(device) {
        const name = device.CustomName || device.Name || "Unnamed device";
        const app = device.AppName ? ` — ${device.AppName}` : "";
        const user = device.LastUserName ? `, ${device.LastUserName}` : "";
        return `${name}${app} (${lastUsedLabel(device.DateLastActivity)}${user})`;
    }

    // Devices, not sessions: a TV that is switched off has no session at all, yet
    // it is exactly the device an installation needs to be bound to.
    function populateDevices(devices, selectedDeviceId) {
        const select = byId("targetDeviceId");
        select.textContent = "";
        select.add(new Option("All devices — not bound", ""));

        devices
            .filter(device => device.Id)
            .sort((left, right) => new Date(right.DateLastActivity || 0) - new Date(left.DateLastActivity || 0))
            .forEach(device => select.add(new Option(deviceLabel(device), device.Id)));

        if (selectedDeviceId && !devices.some(device => device.Id === selectedDeviceId)) {
            // Keep a binding to a device Jellyfin has since forgotten visible and
            // intact, instead of silently resetting it to "all devices" on save.
            select.add(new Option("Saved device (no longer known to Jellyfin)", selectedDeviceId));
        }

        select.value = selectedDeviceId || "";
        byId("targetDeviceStatus").textContent = devices.length === 0
            ? "Jellyfin does not know any playback devices yet. Play something on the TV once, then reload this page."
            : `${devices.length} known devices, most recently used first.`;
    }

    function loadDevices(selectedDeviceId) {
        return window.ApiClient.getJSON(window.ApiClient.getUrl("Devices"))
            .then(result => populateDevices(result.Items || [], selectedDeviceId))
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
        } else {
            show("wledCandidatesContainer", false);
            show("manualWledContainer", true);
            byId("wledFinderStatus").textContent = "No WLED controller found. Enter the address yourself below.";
        }
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
                numericFields.forEach(field => {
                    const key = fieldKey(field);
                    const value = field === "realtimeProtocol"
                        ? ({ Auto: 0, HyperionRawRgb: 1, Ddp: 2 }[config[key]] ?? config[key] ?? defaults[key])
                        : config[key] ?? defaults[key];
                    byId(field).value = value;
                });
                byId("wledHost").value = config.WledHost || "";
                setDelayLabel();
                setAnalysisLabel();
                return loadDevices(config.TargetDeviceId || "");
            })
            .then(findWled)
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
            ConfigSchemaVersion: 2,
            Enabled: byId("enabled").checked,
            TargetDeviceId: byId("targetDeviceId").value,
            WledHost: hostName,
            AnalysisHeight: analysisHeight()
        };
        numericFields.forEach(field => { config[fieldKey(field)] = Number(byId(field).value); });
        if (hostPort) {
            config.WledHttpPort = Number(hostPort);
        }

        window.ApiClient.updatePluginConfiguration(pluginId, config)
            .then(Dashboard.processPluginConfigurationUpdateResult)
            .finally(() => Dashboard.hideLoadingMsg());
    }

    view.addEventListener("viewshow", load);
    byId("realtimeAmbilightConfigurationForm").addEventListener("submit", save);
    byId("outputDelayMilliseconds").addEventListener("input", setDelayLabel);
    byId("analysisWidth").addEventListener("input", setAnalysisLabel);
    byId("findWled").addEventListener("click", findWled);
    byId("refreshDevices").addEventListener("click", () => loadDevices(byId("targetDeviceId").value));
}
