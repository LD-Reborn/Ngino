const state = {
  summary: null,
  detail: null,
  groupDetail: null,
  newKey: null,
  newClientKey: null,
  loading: false,
  usageData: null
};

const content = document.getElementById("content");
const notice = document.getElementById("notice");
const pageTitle = document.getElementById("pageTitle");
const pageSubtitle = document.getElementById("pageSubtitle");
const sidebarMeta = document.getElementById("sidebarMeta");

document.getElementById("refreshButton").addEventListener("click", () => refresh(true));
window.addEventListener("hashchange", () => renderRoute());
document.addEventListener("click", (event) => {
  if (event.target.closest(".help-button") || event.target.closest(".help-popover")) {
    return;
  }

  document.querySelectorAll(".help-popover").forEach((popover) => popover.remove());
  document.querySelectorAll(".help-button.active").forEach((button) => button.classList.remove("active"));
});

const morphdomOptions = {
  childrenOnly: true,
  onBeforeElUpdated(fromEl, toEl) {
    if (fromEl.isEqualNode(toEl)) return false;
    return true;
  }
};

function patchContent(html) {
  const temp = document.createElement("section");
  temp.innerHTML = html;
  morphdom(content, temp, morphdomOptions);
}

function patchSidebar(html) {
  const temp = document.createElement("div");
  temp.innerHTML = html;
  morphdom(sidebarMeta, temp, morphdomOptions);
}

function renderFieldLabel(forId, label, helpText) {
  return `
    <label for="${escapeAttr(forId)}">
      <span>${escapeHtml(label)}</span>
      <button class="help-button" type="button" data-action="toggle-help" data-help="${escapeAttr(helpText)}" aria-label="Explain ${escapeAttr(label)}">?</button>
    </label>
  `;
}

function toggleHelpPopover(button) {
  const field = button.closest(".field");
  if (!field) {
    return;
  }

  const existingPopover = field.querySelector(".help-popover");
  if (existingPopover) {
    existingPopover.remove();
    button.classList.remove("active");
    return;
  }

  document.querySelectorAll(".help-popover").forEach((popover) => popover.remove());
  document.querySelectorAll(".help-button.active").forEach((activeButton) => activeButton.classList.remove("active"));

  const popover = document.createElement("div");
  popover.className = "help-popover";
  popover.textContent = button.dataset.help || "";
  field.appendChild(popover);
  button.classList.add("active");
}

content.addEventListener("click", async (event) => {
  const button = event.target.closest("button[data-action]");
  if (!button) {
    return;
  }

  const { action, clientId, model, keyId, groupId, memberId } = button.dataset;

  if (action === "toggle-help") {
    event.stopPropagation();
    toggleHelpPopover(button);
    return;
  }

  try {
    setBusy(button, true);

    if (action === "disable-hour") {
      await api(`/clients/${encodeURIComponent(clientId)}/disable`, {
        method: "POST",
        body: { mode: "duration", durationMinutes: 60 }
      });
      setNotice(`Disabled ${clientId} for one hour.`);
      await refresh();
    }

    if (action === "disable-manual") {
      await api(`/clients/${encodeURIComponent(clientId)}/disable`, {
        method: "POST",
        body: { mode: "manual" }
      });
      setNotice(`Disabled ${clientId}.`);
      await refresh();
    }

    if (action === "enable-client") {
      await api(`/clients/${encodeURIComponent(clientId)}/enable`, { method: "POST" });
      setNotice(`Enabled ${clientId}.`);
      await refresh();
    }

    if (action === "model-command") {
      await runModelCommand(clientId, model, button.dataset.modelAction);
    }

    if (action === "delete-key") {
      if (!confirm("Delete this user key?")) {
        return;
      }

      await api(`/user-keys/${encodeURIComponent(keyId)}`, { method: "DELETE" });
      setNotice("User key deleted.");
      await refresh();
    }

    if (action === "delete-client-key") {
      if (!confirm("Delete this client key?")) {
        return;
      }

      await api(`/client-keys/${encodeURIComponent(keyId)}`, { method: "DELETE" });
      setNotice("Client key deleted.");
      await refresh();
    }

    if (action === "copy-key") {
      await navigator.clipboard.writeText(button.dataset.key);
      setNotice("User key copied.");
    }

    if (action === "delete-group") {
      if (!confirm("Delete this group and all its clients and key assignments?")) {
        return;
      }

      await api(`/groups/${encodeURIComponent(groupId)}`, { method: "DELETE" });
      setNotice("Group deleted.");
      window.location.hash = "#groups";
      await refresh();
    }

    if (action === "remove-client") {
      if (!confirm("Remove this client from the group?")) {
        return;
      }

      await api(`/groups/${encodeURIComponent(groupId)}/clients/${memberId}`, { method: "DELETE" });
      setNotice("Client removed.");
      await loadGroupDetail(groupId);
    }

    if (action === "toggle-key-assignment") {
      await toggleUserKeyAssignment(groupId, keyId, button.dataset.assigned === "true");
    }

    if (action === "delete-billing-rule") {
      const ruleId = button.dataset.ruleId;
      if (!confirm("Delete this billing rule?")) {
        return;
      }

      await api(`/groups/${encodeURIComponent(groupId)}/billing/rules/${ruleId}`, { method: "DELETE" });
      setNotice("Billing rule deleted.");
      await loadGroupDetail(groupId);
    }

    if (action === "delete-payment") {
      const paymentId = button.dataset.paymentId;
      if (!confirm("Delete this payment record?")) {
        return;
      }

      await api(`/groups/${encodeURIComponent(groupId)}/billing/payments/${paymentId}`, { method: "DELETE" });
      setNotice("Payment deleted.");
      await loadGroupDetail(groupId);
    }
  } catch (error) {
    setNotice(error.message, true);
  } finally {
    setBusy(button, false);
  }
});

content.addEventListener("submit", async (event) => {
  const form = event.target;
  if (!(form instanceof HTMLFormElement)) {
    return;
  }

  event.preventDefault();
  const data = Object.fromEntries(new FormData(form).entries());
  const submit = form.querySelector("button[type=submit]");

  try {
    setBusy(submit, true);

    if (form.dataset.form === "model-action") {
      await runModelCommand(data.clientId, data.model, data.action);
      form.reset();
    }

    if (form.dataset.form === "model-detail") {
      await loadModelDetail(data.model, data.clientId);
    }

    if (form.dataset.form === "user-key") {
      state.newKey = await api("/user-keys", {
        method: "POST",
        body: { name: data.name }
      });
      setNotice("User key created.");
      await refresh();
    }

    if (form.dataset.form === "client-key") {
      state.newClientKey = await api("/client-keys", {
        method: "POST",
        body: { name: data.name }
      });
      setNotice("Client key created.");
      await refresh();
    }

    if (form.dataset.form === "create-group") {
      const result = await api("/groups", {
        method: "POST",
        body: { name: data.name }
      });
      setNotice("Group created.");
      form.reset();
      await refresh();
      window.location.hash = `#groups/${encodeURIComponent(result.id)}`;
    }

    if (form.dataset.form === "edit-group-name") {
      const groupId = form.dataset.groupId;
      await api(`/groups/${encodeURIComponent(groupId)}`, {
        method: "PUT",
        body: { name: data.name }
      });
      setNotice("Group name updated.");
      await refresh();
      await loadGroupDetail(groupId);
    }

    if (form.dataset.form === "add-client") {
      const groupId = form.dataset.groupId;
      const body = {};
      if (data.clientId && data.clientId.trim()) {
        body.clientId = data.clientId.trim();
      }
      if (data.model && data.model.trim()) {
        body.model = data.model.trim();
      }
      if (data.clientPattern && data.clientPattern.trim()) {
        body.clientPattern = data.clientPattern.trim();
      }
      body.keepaliveInstancesToKeepAlive = parseInt(data.keepaliveInstancesToKeepAlive, 10) || 1;
      body.keepaliveMaxParallelismPerClient = parseInt(data.keepaliveMaxParallelismPerClient, 10) || 1;
      body.keepaliveParallelismHeadroom = parseInt(data.keepaliveParallelismHeadroom, 10) || 1;

      if (!body.clientId && !body.clientPattern) {
        throw new Error("Either Client ID or Client pattern is required.");
      }

      await api(`/groups/${encodeURIComponent(groupId)}/clients`, {
        method: "POST",
        body
      });
      setNotice("Client added.");
      form.reset();
      await loadGroupDetail(groupId);
    }

    if (form.dataset.form === "billing-config") {
      const groupId = form.dataset.groupId;
      await api(`/groups/${encodeURIComponent(groupId)}/billing`, {
        method: "PUT",
        body: {
          currency: data.currency,
          defaultRatePer1k: parseFloat(data.defaultRatePer1k) || 0,
          refuseBelowBalance: parseFloat(data.refuseBelowBalance) || 0,
          enabled: data.enabled === "on"
        }
      });
      setNotice("Billing configuration saved.");
      await loadGroupDetail(groupId);
    }

    if (form.dataset.form === "add-billing-rule") {
      const groupId = form.dataset.groupId;
      await api(`/groups/${encodeURIComponent(groupId)}/billing/rules`, {
        method: "POST",
        body: {
          modelRegex: data.modelRegex,
          ratePer1k: parseFloat(data.ratePer1k) || 0
        }
      });
      setNotice("Billing rule added.");
      form.reset();
      await loadGroupDetail(groupId);
    }

    if (form.dataset.form === "add-payment") {
      const groupId = form.dataset.groupId;
      await api(`/groups/${encodeURIComponent(groupId)}/billing/payments`, {
        method: "POST",
        body: {
          amount: parseFloat(data.amount) || 0,
          description: data.description || null
        }
      });
      setNotice("Payment recorded.");
      form.reset();
      await loadGroupDetail(groupId);
    }
  } catch (error) {
    setNotice(error.message, true);
  } finally {
    setBusy(submit, false);
  }
});

async function boot() {
  await refresh();
  setInterval(() => refresh(), 15000);
}

async function refresh(showNotice = false, render = true) {
  state.loading = true;

  try {
    state.summary = await api("/summary");
    updateShell();
    if (render) {
      await renderRoute(false);
    }

    if (showNotice) {
      setNotice("Refreshed.");
    }
  } catch (error) {
    setNotice(error.message, true);
    patchContent(`<div class="panel"><div class="empty">${escapeHtml(error.message)}</div></div>`);
  } finally {
    state.loading = false;
  }
}

async function api(path, options = {}) {
  const headers = options.headers ? { ...options.headers } : {};
  const init = {
    method: options.method || "GET",
    credentials: "same-origin",
    headers
  };

  if (options.body !== undefined) {
    headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(options.body);
  }

  const response = await fetch(`/api/admin${path}`, init);
  const contentType = response.headers.get("content-type") || "";
  const body = contentType.includes("application/json")
    ? await response.json()
    : await response.text();

  if (!response.ok) {
    const message = body?.detail || body?.error || body?.title || response.statusText;
    throw new Error(message);
  }

  return body;
}

async function renderRoute(showLoading = true) {
  const hash = (window.location.hash || "#clients").slice(1);
  const parts = hash.split("/");
  const view = parts[0];
  const encodedParam = parts[1];

  document.querySelectorAll("[data-nav]").forEach((link) => {
    link.classList.toggle("active", link.dataset.nav === view);
  });

  if (view === "models" && encodedParam) {
    const model = decodeURIComponent(encodedParam);
    await loadModelDetail(model, undefined, showLoading);
    return;
  }

  if (view === "groups" && encodedParam) {
    const groupId = decodeURIComponent(encodedParam);
    await loadGroupDetail(groupId, showLoading);
    return;
  }

  state.detail = null;
  state.groupDetail = null;

  if (view === "models") {
    renderModels();
    return;
  }

  if (view === "user-keys") {
    renderUserKeys();
    return;
  }

  if (view === "client-keys") {
    renderClientKeys();
    return;
  }

  if (view === "groups") {
    renderGroups();
    return;
  }

  if (view === "usage") {
    await loadUsage(showLoading);
    return;
  }

  renderClients();
}

function updateShell() {
  const summary = state.summary;
  if (!summary) {
    return;
  }

  patchSidebar(`
    <div>${escapeHtml(summary.user?.name || "Signed in")}</div>
    <div>${summary.clients.length} clients</div>
    <div>${summary.models.length} models</div>
    <div>${(summary.clientKeys || []).length} client keys</div>
    <div>${(summary.userKeys || []).length} user keys</div>
    <div>${(summary.groups || []).length} groups</div>
    <div>${formatDate(summary.generatedAtUtc)}</div>
  `);
}

function renderClients() {
  const clients = state.summary?.clients || [];
  pageTitle.textContent = "Clients";
  pageSubtitle.textContent = "Connected tunnel clients, request counts, and forwarding controls.";

  patchContent(`
    <div class="panel">
      <div class="panel-header">
        <h2>Clients</h2>
        <span class="badge">${clients.length} total</span>
      </div>
      <div class="table-wrap">
        ${clients.length ? clientsTable(clients) : emptyState("No clients have connected yet.")}
      </div>
    </div>
  `);
}

function clientsTable(clients) {
  const clientGroups = state.summary?.clientGroups || {};
  const rows = clients.map((client) => {
    const groups = clientGroups[client.id] || [];
    return `
    <tr>
      <td>
        <div class="cell-main">${escapeHtml(client.id)}</div>
        <div class="cell-sub">${client.connected ? "Connected" : "Offline"}${client.modelsUpdatedAt ? `, models ${formatDate(client.modelsUpdatedAt)}` : ""}</div>
      </td>
      <td>
        <div class="badge-row">
          ${client.connected ? badge("Connected", "good") : badge("Offline", "")}
          ${client.disabled ? badge(client.disabledManually ? "Disabled manual" : "Disabled timed", "bad") : badge("Enabled", "good")}
        </div>
        ${client.disabled ? `<div class="cell-sub">${escapeHtml(disabledText(client))}</div>` : ""}
      </td>
      <td>${number(client.pendingRequests)}</td>
      <td>
        <div class="cell-main">${number(client.requestStats.total)}</div>
        <div class="cell-sub">${number(client.requestStats.last10Minutes)} in 10m, ${number(client.requestStats.lastHour)} in 1h</div>
      </td>
      <td class="col-models">${modelBadges(client.models)}</td>
      <td>${modelBadges(client.activeModels)}</td>
      <td>
        <div class="badge-row">
          ${groups.length ? groups.map((g) => `<a class="badge" href="#groups/${encodeURIComponent(g)}">${escapeHtml(g)}</a>`).join("") : `<span class="cell-sub">None</span>`}
        </div>
      </td>
      <td>
        <div class="actions">
          <button class="button warning" data-action="disable-hour" data-client-id="${escapeAttr(client.id)}" ${client.disabled ? "disabled" : ""}>Disable 1h</button>
          <button class="button warning" data-action="disable-manual" data-client-id="${escapeAttr(client.id)}" ${client.disabled ? "disabled" : ""}>Disable</button>
          <button class="button secondary" data-action="enable-client" data-client-id="${escapeAttr(client.id)}" ${client.disabled ? "" : "disabled"}>Enable</button>
        </div>
      </td>
    </tr>
  `}).join("");

  return `
    <table>
      <thead>
        <tr>
          <th>Client</th>
          <th>Status</th>
          <th>Pending</th>
          <th>Requests</th>
          <th class="col-models">Listed models</th>
          <th>Active models</th>
          <th>Groups</th>
          <th>Actions</th>
        </tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
}

function renderModels() {
  const models = state.summary?.models || [];
  const clients = connectedClients();
  pageTitle.textContent = "Models";
  pageSubtitle.textContent = "Listed and active models, recent request volume, and model operations.";

  patchContent(`
    <div class="panel">
      <div class="panel-header">
        <h2>Run model action</h2>
      </div>
      <div class="panel-body">
        ${modelActionForm(clients)}
      </div>
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>Models</h2>
        <span class="badge">${models.length} total</span>
      </div>
      <div class="table-wrap">
        ${models.length ? modelsTable(models) : emptyState("No models have been reported yet.")}
      </div>
    </div>
  `);
}

function modelActionForm(clients, selectedModel = "", selectedClient = "") {
  return `
    <form class="form-row" data-form="model-action">
      <div class="field">
        <label for="modelActionClient">Client</label>
        <select class="select" id="modelActionClient" name="clientId" required>
          ${clientOptions(clients, selectedClient)}
        </select>
      </div>
      <div class="field">
        <label for="modelActionModel">Model</label>
        <input class="input" id="modelActionModel" name="model" value="${escapeAttr(selectedModel)}" placeholder="llama3.1" required>
      </div>
      <div class="field">
        <label for="modelActionAction">Action</label>
        <select class="select" id="modelActionAction" name="action" required>
          <option value="add">Add</option>
          <option value="load">Load</option>
          <option value="unload">Unload</option>
          <option value="remove">Remove</option>
        </select>
      </div>
      <button class="button" type="submit" ${clients.length ? "" : "disabled"}>Run</button>
    </form>
  `;
}

function modelsTable(models) {
  const rows = models.map((model) => `
    <tr>
      <td>
        <div class="cell-main">${escapeHtml(model.name)}</div>
        <div class="cell-sub">${number(model.metrics.totalRequests)} total requests</div>
      </td>
      <td>${modelBadges(model.listedClients)}</td>
      <td>${modelBadges(model.activeClients)}</td>
      <td>
        <div class="cell-main">${number(model.metrics.requestsLast10Minutes)}</div>
        <div class="cell-sub">${number(model.metrics.requestsLastHour)} in last hour</div>
      </td>
      <td>
        <div class="cell-main">${number(model.metrics.tokensLast10Minutes)}</div>
        <div class="cell-sub">${number(model.metrics.tokensLastHour)} in last hour</div>
      </td>
      <td>
        <a class="button secondary" href="#models/${encodeURIComponent(model.name)}">Details</a>
      </td>
    </tr>
  `).join("");

  return `
    <table>
      <thead>
        <tr>
          <th>Model</th>
          <th>Listed on</th>
          <th>Active on</th>
          <th>Requests 10m</th>
          <th>Tokens 10m</th>
          <th></th>
        </tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
}

async function loadModelDetail(model, clientId, showLoading = true) {
  pageTitle.textContent = "Model Detail";
  pageSubtitle.textContent = model;
  if (showLoading) {
    patchContent(`<div class="panel"><div class="empty">Loading model detail...</div></div>`);
  }

  const query = new URLSearchParams({ model });
  if (clientId) {
    query.set("clientId", clientId);
  }

  state.detail = await api(`/models/detail?${query.toString()}`);
  renderModelDetail();
}

function renderModelDetail() {
  const detail = state.detail;
  const clients = connectedClients();
  const model = detail.model;
  const selectedClient = detail.selectedClientId || clients[0]?.id || "";
  const metrics = detail.metrics;
  const showBody = detail.show?.body === undefined ? null : detail.show.body;

  pageTitle.textContent = "Model Detail";
  pageSubtitle.textContent = model;

  patchContent(`
    <div class="toolbar">
      <a class="button secondary" href="#models">Back to models</a>
    </div>
    <div class="metric-grid">
      ${metric(number(metrics.requestsLast10Minutes), "Requests in 10 minutes")}
      ${metric(number(metrics.requestsLastHour), "Requests in 1 hour")}
      ${metric(number(metrics.tokensLast10Minutes), "Tokens in 10 minutes")}
      ${metric(number(metrics.tokensLastHour), "Tokens in 1 hour")}
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>Placement</h2>
      </div>
      <div class="panel-body">
        <div class="badge-row">
          ${badge("Listed", detail.listedClients.length ? "good" : "")}
          ${modelBadges(detail.listedClients)}
        </div>
        <div class="badge-row" style="margin-top:8px">
          ${badge("Active", detail.activeClients.length ? "good" : "warn")}
          ${modelBadges(detail.activeClients)}
        </div>
      </div>
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>Client action</h2>
      </div>
      <div class="panel-body">
        <form class="form-row" data-form="model-detail">
          <input type="hidden" name="model" value="${escapeAttr(model)}">
          <div class="field">
            <label for="detailClient">Client</label>
            <select class="select" id="detailClient" name="clientId" required>
              ${clientOptions(clients, selectedClient)}
            </select>
          </div>
          <button class="button secondary" type="submit" ${clients.length ? "" : "disabled"}>Refresh detail</button>
        </form>
        <div class="actions" style="margin-top:10px">
          <button class="button secondary" data-action="model-command" data-model-action="load" data-client-id="${escapeAttr(selectedClient)}" data-model="${escapeAttr(model)}" ${selectedClient ? "" : "disabled"}>Load</button>
          <button class="button secondary" data-action="model-command" data-model-action="unload" data-client-id="${escapeAttr(selectedClient)}" data-model="${escapeAttr(model)}" ${selectedClient ? "" : "disabled"}>Unload</button>
          <button class="button danger" data-action="model-command" data-model-action="remove" data-client-id="${escapeAttr(selectedClient)}" data-model="${escapeAttr(model)}" ${selectedClient ? "" : "disabled"}>Remove</button>
        </div>
      </div>
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>Ollama show response</h2>
        ${detail.show ? badge(detail.show.ok ? "OK" : `HTTP ${detail.show.statusCode}`, detail.show.ok ? "good" : "bad") : ""}
      </div>
      <div class="panel-body">
        ${detail.show ? `<pre class="pre">${escapeHtml(formatJson(showBody))}</pre>` : emptyState("No connected client was available for details.")}
      </div>
    </div>
  `);
}

async function runModelCommand(clientId, model, action) {
  if (!clientId || !model || !action) {
    throw new Error("Client, model, and action are required.");
  }

  if (action === "remove" && !confirm(`Remove ${model} from ${clientId}?`)) {
    return;
  }

  const result = await api("/models/actions", {
    method: "POST",
    body: { clientId, model, action }
  });

  if (!result.ok) {
    throw new Error(modelActionError(result));
  }

  const detail = modelActionResultDetail(result);
  const completed = `${capitalize(action)} completed for ${model} on ${clientId}${detail ? ` (${detail})` : ""}.`;

  if (action === "load" || action === "unload") {
    const shouldBeActive = action === "load";
    setNotice(`${completed} Waiting for active model snapshot.`);

    if (await waitForModelActiveState(clientId, model, shouldBeActive)) {
      setNotice(completed);
      return;
    }

    setNotice(`${completed} The active model snapshot has not reflected the change yet.`);
    return;
  }

  setNotice(completed);
  await refreshAfterModelCommand(model, clientId);
}

function renderUserKeys() {
  const keys = state.summary?.userKeys || [];
  pageTitle.textContent = "User Keys";
  pageSubtitle.textContent = "Keys accepted by the proxy token header, bearer auth, query token, and token path.";

  patchContent(`
    ${state.newKey ? newKeyPanel(state.newKey) : ""}
    <div class="panel">
      <div class="panel-header">
        <h2>Create user key</h2>
      </div>
      <div class="panel-body">
        <form class="form-row" data-form="user-key">
          <div class="field">
            <label for="userKeyName">Name</label>
            <input class="input" id="userKeyName" name="name" placeholder="e.g. openwebui_prod" required>
          </div>
          <button class="button" type="submit">Create</button>
        </form>
      </div>
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>User keys</h2>
        <span class="badge">${keys.length} total</span>
      </div>
      <div class="table-wrap">
        ${keys.length ? userKeysTable(keys) : emptyState("No user keys have been created.")}
      </div>
    </div>
  `);
}

function renderClientKeys() {
  const keys = state.summary?.clientKeys || [];
  pageTitle.textContent = "Client Keys";
  pageSubtitle.textContent = "Keys accepted by GPU clients to establish tunnel connections.";

  patchContent(`
    ${state.newClientKey ? newKeyPanel(state.newClientKey) : ""}
    <div class="panel">
      <div class="panel-header">
        <h2>Create client key</h2>
      </div>
      <div class="panel-body">
        <form class="form-row" data-form="client-key">
          <div class="field">
            <label for="clientKeyName">Name</label>
            <input class="input" id="clientKeyName" name="name" placeholder="e.g. gpu_workstation_1" required>
          </div>
          <button class="button" type="submit">Create</button>
        </form>
      </div>
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>Client keys</h2>
        <span class="badge">${keys.length} total</span>
      </div>
      <div class="table-wrap">
        ${keys.length ? clientKeysTable(keys) : emptyState("No client keys have been created.")}
      </div>
    </div>
  `);
}

function newKeyPanel(key) {
  return `
    <div class="new-key">
      <strong>New user key</strong>
      <code>${escapeHtml(key.key)}</code>
      <div class="actions">
        <button class="button secondary" type="button" data-action="copy-key" data-key="${escapeAttr(key.key)}">Copy</button>
      </div>
    </div>
  `;
}

function userKeysTable(keys) {
  const rows = keys.map((key) => `
    <tr>
      <td>
        <div class="cell-main">${escapeHtml(key.name)}</div>
        <div class="cell-sub">${escapeHtml(key.keyPrefix)}...</div>
      </td>
      <td>${formatDate(key.createdAtUtc)}</td>
      <td>${key.lastUsedUtc ? formatDate(key.lastUsedUtc) : "Never"}</td>
      <td>
        <button class="button danger" data-action="delete-key" data-key-id="${escapeAttr(key.id)}">Delete</button>
      </td>
    </tr>
  `).join("");

  return `
    <table>
      <thead>
        <tr>
          <th>Name</th>
          <th>Created</th>
          <th>Last used</th>
          <th></th>
        </tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
}

function clientKeysTable(keys) {
  const rows = keys.map((key) => `
    <tr>
      <td>
        <div class="cell-main">${escapeHtml(key.name)}</div>
        <div class="cell-sub">${escapeHtml(key.keyPrefix)}...</div>
      </td>
      <td>${formatDate(key.createdAtUtc)}</td>
      <td>${key.lastUsedUtc ? formatDate(key.lastUsedUtc) : "Never"}</td>
      <td>
        <button class="button danger" data-action="delete-client-key" data-key-id="${escapeAttr(key.id)}">Delete</button>
      </td>
    </tr>
  `).join("");

  return `
    <table>
      <thead>
        <tr>
          <th>Name</th>
          <th>Created</th>
          <th>Last used</th>
          <th></th>
        </tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
}

function renderGroups() {
  const groups = state.summary?.groups || [];
  pageTitle.textContent = "Groups";
  pageSubtitle.textContent = "Manage access groups that control which clients and models user keys can reach.";

  patchContent(`
    <div class="panel">
      <div class="panel-header">
        <h2>Create group</h2>
      </div>
      <div class="panel-body">
        <form class="form-row" data-form="create-group">
          <div class="field">
            <label for="groupName">Name</label>
            <input class="input" id="groupName" name="name" placeholder="e.g. embeddings" required>
          </div>
          <button class="button" type="submit">Create</button>
        </form>
      </div>
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>Groups</h2>
        <span class="badge">${groups.length} total</span>
      </div>
      <div class="table-wrap">
        ${groups.length ? groupsTable(groups) : emptyState("No groups have been created.")}
      </div>
    </div>
  `);
}

function groupsTable(groups) {
  const rows = groups.map((group) => `
    <tr>
      <td>
        <a class="cell-main group-link" href="#groups/${encodeURIComponent(group.id)}">${escapeHtml(group.name)}</a>
      </td>
      <td>${formatDate(group.createdAtUtc)}</td>
      <td>
        <div class="actions">
          <a class="button secondary" href="#groups/${encodeURIComponent(group.id)}">Edit</a>
          <button class="button danger" data-action="delete-group" data-group-id="${escapeAttr(group.id)}">Delete</button>
        </div>
      </td>
    </tr>
  `).join("");

  return `
    <table>
      <thead>
        <tr>
          <th>Name</th>
          <th>Created</th>
          <th></th>
        </tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
}

async function loadGroupDetail(groupId, showLoading = true) {
  pageTitle.textContent = "Group Detail";
  pageSubtitle.textContent = groupId;
  if (showLoading) {
    patchContent(`<div class="panel"><div class="empty">Loading group...</div></div>`);
  }

  try {
    const [group, clients, userKeyGroups, billing, rules, payments, balance] = await Promise.all([
      api(`/groups/${encodeURIComponent(groupId)}`),
      api(`/groups/${encodeURIComponent(groupId)}/clients`),
      api("/user-keys/groups"),
      api(`/groups/${encodeURIComponent(groupId)}/billing`),
      api(`/groups/${encodeURIComponent(groupId)}/billing/rules`),
      api(`/groups/${encodeURIComponent(groupId)}/billing/payments`),
      api(`/groups/${encodeURIComponent(groupId)}/billing/balance`)
    ]);

    state.groupDetail = { group, clients, userKeyGroups, billing, rules, payments, balance };
    renderGroupDetail();
  } catch (error) {
    patchContent(`<div class="panel"><div class="empty">${escapeHtml(error.message)}</div></div>`);
  }
}

function renderGroupDetail() {
  const { group, clients, userKeyGroups, billing, rules, payments, balance } = state.groupDetail;
  const allUserKeys = state.summary?.userKeys || [];
  const assignedKeyIds = new Set(
    userKeyGroups
      .filter((akg) => akg.userKeyId && (akg.groupIds || []).includes(group.id))
      .map((akg) => akg.userKeyId)
  );

  pageTitle.textContent = "Group Detail";
  pageSubtitle.textContent = group.name;

  patchContent(`
    <div class="toolbar">
      <a class="button secondary" href="#groups">Back to groups</a>
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>Group name</h2>
      </div>
      <div class="panel-body">
        <form class="form-row" data-form="edit-group-name" data-group-id="${escapeAttr(group.id)}">
          <div class="field">
            <label for="editGroupName">Name</label>
            <input class="input" id="editGroupName" name="name" value="${escapeAttr(group.name)}" required>
          </div>
          <button class="button" type="submit">Save</button>
        </form>
      </div>
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>Add client</h2>
      </div>
      <div class="panel-body">
        <form class="form-row" data-form="add-client" data-group-id="${escapeAttr(group.id)}">
          <div class="field">
            <label for="addClientClientId">Client ID</label>
            <input class="input" id="addClientClientId" name="clientId" placeholder="Client_1">
          </div>
          <div class="field">
            <label for="addClientModel">Model (optional)</label>
            <input class="input" id="addClientModel" name="model" placeholder="bge-m3">
          </div>
          <div class="field">
            <label for="addClientPattern">Client pattern (regex, optional)</label>
            <input class="input" id="addClientPattern" name="clientPattern" placeholder="GPU_[0-9]*">
          </div>
          <div class="field">
            ${renderFieldLabel("addClientKeepaliveInstances", "Min always loaded instances", "The minimum number of warm model instances that should stay loaded and ready so requests do not wait for a cold start.")}
            <input class="input" id="addClientKeepaliveInstances" name="keepaliveInstancesToKeepAlive" type="number" min="1" step="1" value="1">
          </div>
          <div class="field">
            ${renderFieldLabel("addClientKeepaliveMaxParallelism", "Max parallelism per client", "The maximum number of concurrent requests this client can handle at once. Higher values let one client absorb more traffic, but too many can overload the GPU.")}
            <input class="input" id="addClientKeepaliveMaxParallelism" name="keepaliveMaxParallelismPerClient" type="number" min="1" step="1" value="1">
          </div>
          <div class="field">
            ${renderFieldLabel("addClientKeepaliveHeadroom", "Parallelism headroom", "How much spare parallelism to leave unused so traffic spikes can be absorbed without saturating the GPU. A larger headroom makes routing more conservative.")}
            <input class="input" id="addClientKeepaliveHeadroom" name="keepaliveParallelismHeadroom" type="number" min="1" step="1" value="1">
          </div>
          <button class="button" type="submit">Add</button>
        </form>
        <div class="cell-sub" style="margin-top:8px">Provide either a Client ID (for explicit client access) or a Client pattern (for regex-based matching). Model is optional to restrict to a specific model. Keepalive defaults to 1 instance, 1 parallelism, and 1 headroom.</div>
      </div>
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>Clients</h2>
        <span class="badge">${clients.length} total</span>
      </div>
      <div class="table-wrap">
        ${clients.length ? groupClientsTable(clients, group.id) : emptyState("No clients in this group.")}
      </div>
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>User key assignments</h2>
      </div>
      <div class="table-wrap">
        ${allUserKeys.length ? userKeyAssignmentTable(allUserKeys, group.id, assignedKeyIds) : emptyState("No user keys have been created.")}
      </div>
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>Billing</h2>
        ${billing.enabled ? badge("Enabled", "good") : badge("Disabled", "")}
      </div>
      <div class="panel-body">
        <form class="form-row" data-form="billing-config" data-group-id="${escapeAttr(group.id)}">
          <div class="field">
            <label for="billingCurrency">Currency</label>
            <select class="select" id="billingCurrency" name="currency">
              ${["EUR", "USD", "GBP", "INR", "JPY", "CAD", "AUD", "CHF"].map(c => `<option value="${c}" ${billing.currency === c ? "selected" : ""}>${c}</option>`).join("")}
            </select>
          </div>
          <div class="field">
            <label for="billingDefaultRate">Default rate / 1k tokens</label>
            <input class="input" id="billingDefaultRate" name="defaultRatePer1k" type="number" step="0.0001" min="0" value="${billing.defaultRatePer1k || 0}">
          </div>
          <div class="field">
            <label for="billingRefuseBelow">Refuse below balance</label>
            <input class="input" id="billingRefuseBelow" name="refuseBelowBalance" type="number" step="0.01" value="${billing.refuseBelowBalance || 0}">
          </div>
          <div class="field billing-toggle">
            <label for="billingEnabled">Enabled</label>
            <input type="checkbox" id="billingEnabled" name="enabled" ${billing.enabled ? "checked" : ""}>
          </div>
          <button class="button" type="submit">Save</button>
        </form>
      </div>
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>Balance</h2>
      </div>
      <div class="panel-body">
        <div class="balance-display">
          <div class="balance-current">
            <strong>${formatCurrency(balance.balance, balance.currency)}</strong>
            <span>Current balance</span>
          </div>
          <div class="balance-detail">
            <div>${formatCurrency(balance.totalPayments, balance.currency)} payments</div>
            <div>${formatCurrency(balance.totalCosts, balance.currency)} costs</div>
          </div>
        </div>
      </div>
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>Billing rules</h2>
        <span class="badge">${rules.length} total</span>
      </div>
      <div class="table-wrap">
        ${rules.length ? billingRulesTable(rules, group.id) : emptyState("No billing rules. The default rate applies to all models.")}
      </div>
      <div class="panel-body">
        <form class="form-row" data-form="add-billing-rule" data-group-id="${escapeAttr(group.id)}">
          <div class="field">
            <label for="ruleModelRegex">Model regex</label>
            <input class="input" id="ruleModelRegex" name="modelRegex" placeholder="llama.*" required>
          </div>
          <div class="field">
            <label for="ruleRate">Rate / 1k tokens</label>
            <input class="input" id="ruleRate" name="ratePer1k" type="number" step="0.0001" min="0" required>
          </div>
          <button class="button" type="submit">Add rule</button>
        </form>
      </div>
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>Payments</h2>
        <span class="badge">${payments.length} total</span>
      </div>
      <div class="table-wrap">
        ${payments.length ? paymentsTable(payments, group.id) : emptyState("No payments recorded.")}
      </div>
      <div class="panel-body">
        <form class="form-row" data-form="add-payment" data-group-id="${escapeAttr(group.id)}">
          <div class="field">
            <label for="paymentAmount">Amount</label>
            <input class="input" id="paymentAmount" name="amount" type="number" step="0.01" min="0" required>
          </div>
          <div class="field">
            <label for="paymentDescription">Description</label>
            <input class="input" id="paymentDescription" name="description" placeholder="Invoice #1234">
          </div>
          <button class="button" type="submit">Record payment</button>
        </form>
      </div>
    </div>
  `);
}

function formatKeepalivePolicy(policy) {
  if (!policy) {
    return `<div class="cell-sub">Default (1 / 1 / 1)</div>`;
  }

  return `
    <div class="cell-main">${policy.instancesToKeepAlive} instance${policy.instancesToKeepAlive === 1 ? "" : "s"}</div>
    <div class="cell-sub">Parallelism ${policy.maxParallelismPerClient} · headroom ${policy.parallelismHeadroom}</div>
  `;
}

function groupClientsTable(clients, groupId) {
  const rows = clients.map((client) => `
    <tr>
      <td>
        ${client.clientId
          ? `<div class="cell-main">${escapeHtml(client.clientId)}</div>`
          : `<div class="cell-sub">-</div>`
        }
      </td>
      <td>
        ${client.model
          ? `<div class="cell-main">${escapeHtml(client.model)}</div>`
          : `<div class="cell-sub">All models</div>`
        }
      </td>
      <td>
        ${client.clientPattern
          ? `<div class="cell-main code-inline">${escapeHtml(client.clientPattern)}</div>`
          : `<div class="cell-sub">-</div>`
        }
      </td>
      <td>${formatKeepalivePolicy(client.keepalivePolicy)}</td>
      <td>
        <button class="button danger" data-action="remove-client" data-group-id="${escapeAttr(groupId)}" data-member-id="${client.id}">Remove</button>
      </td>
    </tr>
  `).join("");

  return `
    <table>
      <thead>
        <tr>
          <th>Client ID</th>
          <th>Model</th>
          <th>Client pattern</th>
          <th>Keepalive policy</th>
          <th></th>
        </tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
}

function userKeyAssignmentTable(userKeys, groupId, assignedKeyIds) {
  const rows = userKeys.map((key) => {
    const isAssigned = assignedKeyIds.has(key.id);
    return `
      <tr>
        <td>
          <div class="cell-main">${escapeHtml(key.name)}</div>
          <div class="cell-sub">${escapeHtml(key.keyPrefix)}...</div>
        </td>
        <td>
          <button class="button ${isAssigned ? "danger" : "secondary"}"
            data-action="toggle-key-assignment"
            data-group-id="${escapeAttr(groupId)}"
            data-key-id="${escapeAttr(key.id)}"
            data-assigned="${isAssigned}">${isAssigned ? "Remove" : "Assign"}</button>
        </td>
      </tr>
    `;
  }).join("");

  return `
    <table>
      <thead>
        <tr>
          <th>User key</th>
          <th></th>
        </tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
}

async function toggleUserKeyAssignment(groupId, keyId, currentlyAssigned) {
  const userKeyGroups = state.groupDetail?.userKeyGroups || [];
  const allUserKeys = state.summary?.userKeys || [];

  const keyGroups = userKeyGroups.find((ukg) => ukg.userKeyId === keyId);
  const currentGroupIds = keyGroups ? [...keyGroups.groupIds] : [];

  let newGroupIds;
  if (currentlyAssigned) {
    newGroupIds = currentGroupIds.filter((id) => id !== groupId);
  } else {
    newGroupIds = [...currentGroupIds, groupId];
  }

  await api(`/user-keys/${encodeURIComponent(keyId)}/groups`, {
    method: "PUT",
    body: { groupIds: newGroupIds }
  });

  setNotice(currentlyAssigned ? "User key unassigned from group." : "User key assigned to group.");
  await loadGroupDetail(groupId);
}

function billingRulesTable(rules, groupId) {
  const rows = rules.map((rule) => `
    <tr>
      <td><div class="code-inline">${escapeHtml(rule.modelRegex)}</div></td>
      <td>${rule.ratePer1k}</td>
      <td>
        <button class="button danger" data-action="delete-billing-rule" data-group-id="${escapeAttr(groupId)}" data-rule-id="${rule.id}">Delete</button>
      </td>
    </tr>
  `).join("");

  return `
    <table>
      <thead>
        <tr>
          <th>Model regex</th>
          <th>Rate / 1k tokens</th>
          <th></th>
        </tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
}

function paymentsTable(payments, groupId) {
  const rows = payments.map((payment) => `
    <tr>
      <td>${formatCurrency(payment.amount, "")}</td>
      <td>${payment.description ? escapeHtml(payment.description) : `<span class="cell-sub">-</span>`}</td>
      <td>${payment.createdBy ? escapeHtml(payment.createdBy) : `<span class="cell-sub">-</span>`}</td>
      <td>${formatDate(payment.createdAtUtc)}</td>
      <td>
        <button class="button danger" data-action="delete-payment" data-group-id="${escapeAttr(groupId)}" data-payment-id="${payment.id}">Delete</button>
      </td>
    </tr>
  `).join("");

  return `
    <table>
      <thead>
        <tr>
          <th>Amount</th>
          <th>Description</th>
          <th>Created by</th>
          <th>Date</th>
          <th></th>
        </tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
}

function formatCurrency(value, currency) {
  const num = typeof value === "number" ? value : parseFloat(value) || 0;
  const formatted = new Intl.NumberFormat(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 4 }).format(num);
  if (currency && typeof currency === "string" && currency.length > 0) {
    return `${formatted} ${currency}`;
  }
  return formatted;
}

async function loadUsage(showLoading = true) {
  pageTitle.textContent = "Usage";
  pageSubtitle.textContent = "Token usage and revenue statistics.";
  if (showLoading) {
    patchContent(`<div class="panel"><div class="empty">Loading usage data...</div></div>`);
  }

  try {
    const [usage, revenue] = await Promise.all([
      api("/usage/tokens"),
      api("/usage/revenue")
    ]);

    state.usageData = { usage, revenue };
    renderUsage();
  } catch (error) {
    patchContent(`<div class="panel"><div class="empty">${escapeHtml(error.message)}</div></div>`);
  }
}

function renderUsage() {
  const { usage, revenue } = state.usageData;
  const hasAnyBilling = (state.summary?.groups || []).some((g) => {
    const billing = state.groupDetail?.billing;
    return billing?.enabled;
  });

  const byModel = usage.byModel || [];
  const byClient = usage.byClient || [];
  const byUserKey = usage.byUserKey || [];
  const byGroup = usage.byGroup || [];
  const clientRevenue = revenue || [];

  const revenueMap = {};
  for (const r of clientRevenue) {
    revenueMap[r.clientId] = r;
  }

  patchContent(`
    <div class="panel">
      <div class="panel-header">
        <h2>Tokens by model</h2>
        <span class="badge">${byModel.length} models</span>
      </div>
      <div class="table-wrap">
        ${byModel.length ? tokenStatsModelTable(byModel) : emptyState("No token data yet.")}
      </div>
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>Tokens by machine</h2>
        <span class="badge">${byClient.length} machines</span>
      </div>
      <div class="table-wrap">
        ${byClient.length ? tokenStatsClientTable(byClient, revenueMap) : emptyState("No token data yet.")}
      </div>
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>Tokens by user key</h2>
        <span class="badge">${byUserKey.length} keys</span>
      </div>
      <div class="table-wrap">
        ${byUserKey.length ? tokenStatsUserKeyTable(byUserKey) : emptyState("No token data yet.")}
      </div>
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>Tokens by group</h2>
        <span class="badge">${byGroup.length} groups</span>
      </div>
      <div class="table-wrap">
        ${byGroup.length ? tokenStatsGroupTable(byGroup) : emptyState("No token data yet.")}
      </div>
    </div>
  `);
}

function tokenStatsModelTable(stats) {
  const rows = stats.map((s) => `
    <tr>
      <td><div class="cell-main">${escapeHtml(s.model)}</div></td>
      <td>${number(s.promptTokens)}</td>
      <td>${number(s.completionTokens)}</td>
      <td>${number(s.totalTokens)}</td>
      <td>${number(s.requests)}</td>
    </tr>
  `).join("");

  return `
    <table>
      <thead>
        <tr>
          <th>Model</th>
          <th>Prompt tokens</th>
          <th>Completion tokens</th>
          <th>Total tokens</th>
          <th>Requests</th>
        </tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
}

function tokenStatsClientTable(stats, revenueMap) {
  const rows = stats.map((s) => {
    const rev = revenueMap[s.clientId];
    return `
    <tr>
      <td><div class="cell-main">${escapeHtml(s.clientId)}</div></td>
      <td>${number(s.promptTokens)}</td>
      <td>${number(s.completionTokens)}</td>
      <td>${number(s.totalTokens)}</td>
      <td>${number(s.requests)}</td>
      ${rev ? `<td>${formatCurrency(rev.revenue, rev.currency)}</td>` : `<td><span class="cell-sub">-</span></td>`}
    </tr>
  `}).join("");

  return `
    <table>
      <thead>
        <tr>
          <th>Machine</th>
          <th>Prompt tokens</th>
          <th>Completion tokens</th>
          <th>Total tokens</th>
          <th>Requests</th>
          <th>Revenue</th>
        </tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
}

function tokenStatsUserKeyTable(stats) {
  const rows = stats.map((s) => `
    <tr>
      <td>
        <div class="cell-main">${escapeHtml(s.userKeyName)}</div>
        <div class="cell-sub">${escapeHtml(s.userKeyPrefix)}...</div>
      </td>
      <td>${number(s.promptTokens)}</td>
      <td>${number(s.completionTokens)}</td>
      <td>${number(s.totalTokens)}</td>
      <td>${number(s.requests)}</td>
    </tr>
  `).join("");

  return `
    <table>
      <thead>
        <tr>
          <th>User key</th>
          <th>Prompt tokens</th>
          <th>Completion tokens</th>
          <th>Total tokens</th>
          <th>Requests</th>
        </tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
}

function tokenStatsGroupTable(stats) {
  const rows = stats.map((s) => `
    <tr>
      <td><div class="cell-main">${escapeHtml(s.groupName)}</div></td>
      <td>${number(s.promptTokens)}</td>
      <td>${number(s.completionTokens)}</td>
      <td>${number(s.totalTokens)}</td>
      <td>${number(s.requests)}</td>
    </tr>
  `).join("");

  return `
    <table>
      <thead>
        <tr>
          <th>Group</th>
          <th>Prompt tokens</th>
          <th>Completion tokens</th>
          <th>Total tokens</th>
          <th>Requests</th>
        </tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
}

function connectedClients() {
  return (state.summary?.clients || []).filter((client) => client.connected);
}

async function waitForModelActiveState(clientId, model, shouldBeActive) {
  const deadline = Date.now() + 15000;

  while (Date.now() <= deadline) {
    await refreshAfterModelCommand(model, clientId);

    const client = (state.summary?.clients || [])
      .find((item) => sameText(item.id, clientId));

    if (client && modelListContains(client.activeModels, model) === shouldBeActive) {
      return true;
    }

    await delay(1000);
  }

  return false;
}

async function refreshAfterModelCommand(model, clientId) {
  await refresh(false, false);

  const hash = window.location.hash || "";
  if (hash.startsWith(`#models/${encodeURIComponent(model)}`)) {
    await loadModelDetail(model, clientId, false);
    return;
  }

  await renderRoute(false);
}

function modelActionResultDetail(result) {
  const body = result?.body;

  if (!body || typeof body === "string") {
    return body || "";
  }

  if (body.status) {
    return body.status;
  }

  if (body.done_reason) {
    return `done: ${body.done_reason}`;
  }

  if (body.done) {
    return "done";
  }

  return "";
}

function modelActionError(result) {
  const body = result?.body;

  if (typeof body === "string" && body) {
    return body;
  }

  if (body?.error) {
    return body.error;
  }

  if (body?.message) {
    return body.message;
  }

  return `Model action failed with HTTP ${result.statusCode}.`;
}

function modelListContains(models, model) {
  return (models || []).some((item) => sameModelName(item, model));
}

function sameModelName(left, right) {
  return stripLatestTag(left).toLowerCase() === stripLatestTag(right).toLowerCase();
}

function sameText(left, right) {
  return String(left || "").trim().toLowerCase() === String(right || "").trim().toLowerCase();
}

function stripLatestTag(model) {
  const value = String(model || "").trim();
  return value.toLowerCase().endsWith(":latest") ? value.slice(0, -":latest".length) : value;
}

function delay(milliseconds) {
  return new Promise((resolve) => setTimeout(resolve, milliseconds));
}

function clientOptions(clients, selectedClient) {
  if (!clients.length) {
    return `<option value="">No connected clients</option>`;
  }

  return clients.map((client) => `
    <option value="${escapeAttr(client.id)}" ${client.id === selectedClient ? "selected" : ""}>${escapeHtml(client.id)}</option>
  `).join("");
}

function modelBadges(items) {
  if (!items || !items.length) {
    return `<span class="cell-sub">None</span>`;
  }

  return `<div class="badge-row">${items.map((item) => badge(item, "")).join("")}</div>`;
}

function badge(text, kind) {
  return `<span class="badge ${kind || ""}">${escapeHtml(text)}</span>`;
}

function metric(value, label) {
  return `
    <div class="metric">
      <strong>${escapeHtml(value)}</strong>
      <span>${escapeHtml(label)}</span>
    </div>
  `;
}

function emptyState(text) {
  return `<div class="empty">${escapeHtml(text)}</div>`;
}

function disabledText(client) {
  if (client.disabledManually) {
    return "Until enabled manually";
  }

  return client.disabledUntilUtc ? `Until ${formatDate(client.disabledUntilUtc)}` : "Disabled";
}

function formatDate(value) {
  if (!value) {
    return "";
  }

  return new Intl.DateTimeFormat(undefined, {
    dateStyle: "short",
    timeStyle: "medium"
  }).format(new Date(value));
}

function formatJson(value) {
  if (typeof value === "string") {
    return value;
  }

  return JSON.stringify(value, null, 2);
}

function number(value) {
  return new Intl.NumberFormat().format(value || 0);
}

function capitalize(value) {
  return value ? value[0].toUpperCase() + value.slice(1) : value;
}

function setNotice(message, isError = false) {
  notice.hidden = false;
  notice.textContent = message;
  notice.classList.toggle("error", isError);

  clearTimeout(setNotice.timer);
  setNotice.timer = setTimeout(() => {
    notice.hidden = true;
  }, isError ? 7000 : 3500);
}

function setBusy(element, busy) {
  if (!element) {
    return;
  }

  element.disabled = busy;
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#039;");
}

function escapeAttr(value) {
  return escapeHtml(value);
}

boot();
