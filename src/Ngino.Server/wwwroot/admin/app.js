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
  if (event.target.closest(".help-button") || event.target.closest(".warn-button") || event.target.closest(".help-popover")) {
    return;
  }

  document.querySelectorAll(".help-popover").forEach((popover) => popover.remove());
  document.querySelectorAll(".help-button.active, .warn-button.active").forEach((button) => button.classList.remove("active"));
});

const morphdomOptions = {
  childrenOnly: true,
  onBeforeElUpdated(fromEl, toEl) {
    if (fromEl.isEqualNode(toEl)) return false;
    return true;
  }
};

function patchContent(html) {
  document.querySelectorAll(".help-popover").forEach((popover) => popover.remove());
  document.querySelectorAll(".help-button.active, .warn-button.active").forEach((button) => button.classList.remove("active"));
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
  const host = button.closest(".field") || button.closest("td") || button.closest("th");
  if (!host) {
    return;
  }

  const existingPopover = document.querySelector(".help-popover[data-owner-id='" + (button.dataset.ownerId || "") + "']") || host.querySelector(".help-popover");
  if (existingPopover) {
    existingPopover.remove();
    button.classList.remove("active");
    return;
  }

  document.querySelectorAll(".help-popover").forEach((popover) => popover.remove());
  document.querySelectorAll(".help-button.active, .warn-button.active").forEach((activeButton) => activeButton.classList.remove("active"));

  const popover = document.createElement("div");
  const inTable = host.tagName === "TD" || host.tagName === "TH";
  popover.className = "help-popover" + (inTable ? " in-table" : "");
  popover.textContent = button.dataset.help || "";

  if (inTable) {
    popover.dataset.ownerId = button.dataset.ownerId || button.textContent;
    positionPopover(popover, button);
    document.body.appendChild(popover);
  } else {
    (button.closest("label") || host).appendChild(popover);
  }

  button.classList.add("active");
}

function positionPopover(popover, anchor) {
  const rect = anchor.getBoundingClientRect();
  popover.style.position = "fixed";
  popover.style.left = `${rect.left}px`;
  popover.style.top = `${rect.bottom + 6}px`;
  popover.style.margin = "0";

  const bounds = popover.getBoundingClientRect();
  if (bounds.bottom > window.innerHeight) {
    popover.style.top = `${Math.max(4, rect.top - bounds.height - 6)}px`;
  }
  if (bounds.right > window.innerWidth) {
    popover.style.left = `${Math.max(4, rect.right - bounds.width)}px`;
  }
}

content.addEventListener("click", async (event) => {
  const warmthEdit = event.target.closest("[data-warmth-edit]");
  if (warmthEdit) {
    event.preventDefault();
    try {
      await editWarmth(warmthEdit.dataset);
    } catch (error) {
      setNotice(error instanceof Error ? error.message : String(error), true);
    }
    return;
  }

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

    if (action === "disable-temporary") {
      setBusy(button, false);
      try {
        const body = await showDisableModal(clientId);
        setBusy(button, true);
        await api(`/clients/${encodeURIComponent(clientId)}/disable`, {
          method: "POST",
          body
        });
        setNotice(`Disabled ${clientId}.`);
        await refresh();
      } catch {
        // cancelled
      }
      return;
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

    if (action === "manage-client-key-groups") {
      setBusy(button, false);
      try {
        const body = await showClientKeyGroupsModal(keyId, button.dataset.keyName);
        setBusy(button, true);
        await api(`/client-keys/${encodeURIComponent(keyId)}/groups`, {
          method: "PUT",
          body
        });
        setNotice("Client key groups updated.");
        await refresh();
      } catch {
        // cancelled
      }
      return;
    }

    if (action === "manage-user-key-groups") {
      setBusy(button, false);
      try {
        const body = await showUserKeyGroupsModal(keyId, button.dataset.keyName);
        setBusy(button, true);
        await api(`/user-keys/${encodeURIComponent(keyId)}/groups`, {
          method: "PUT",
          body
        });
        setNotice("User key groups updated.");
        await refresh();
      } catch {
        // cancelled
      }
      return;
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

    if (action === "toggle-client-key-assignment") {
      await toggleClientKeyAssignment(groupId, keyId, button.dataset.assigned === "true");
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

    if (form.dataset.form === "add-model") {
      const groupId = form.dataset.groupId;
      const model = data.model ? data.model.trim() : "";
      if (!model) {
        throw new Error("Model is required.");
      }
      const availableModels = (state.summary?.models || []).map((m) => m.name);
      if (availableModels.length && !availableModels.some((name) => modelSelectorMatches(model, name))) {
        const confirmed = await showConfirmModal(
          "The model selector you entered does not currently select any available models. Are you sure?",
          "Add anyway"
        ).then(() => true, () => false);
        if (!confirmed) {
          form.reset();
          return;
        }
      }
      const body = {
        model,
        keepaliveInstancesToKeepAlive: parseInt(data.keepaliveInstancesToKeepAlive, 10) || 0,
        keepaliveMaxParallelismPerClient: parseInt(data.keepaliveMaxParallelismPerClient, 10) || 1,
        keepaliveParallelismHeadroom: parseInt(data.keepaliveParallelismHeadroom, 10) || 0
      };

      await api(`/groups/${encodeURIComponent(groupId)}/clients`, {
        method: "POST",
        body
      });
      setNotice("Model added.");
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
  const groupNames = Object.fromEntries((state.summary?.groups || []).map((g) => [g.id, g.name]));
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
          ${clientWarmthBadge(client)}
        </div>
        ${client.disabled ? `<div class="cell-sub">${escapeHtml(disabledText(client))}</div>` : ""}
        ${isScheduled(client) ? `<div class="cell-sub">${escapeHtml(disabledText(client))}</div>` : ""}
      </td>
      <td>${number(client.pendingRequests)}</td>
      <td>
        <div class="cell-main">${number(client.requestStats.total)}</div>
        <div class="cell-sub">${number(client.requestStats.last10Minutes)} in 10m, ${number(client.requestStats.lastHour)} in 1h</div>
      </td>
      <td class="col-models">${warmthModelBadges(client, client.models)}</td>
      <td>${warmthModelBadges(client, client.activeModels)}</td>
      <td>
        <div class="badge-row">
          ${groups.length ? groups.map((g) => `<a class="badge" href="#groups/${encodeURIComponent(g)}">${escapeHtml(groupNames[g] || g)}</a>`).join("") : `<span class="cell-sub">None</span>`}
        </div>
      </td>
      <td>
        <div class="actions">
          <button class="button warning" data-action="disable-temporary" data-client-id="${escapeAttr(client.id)}" ${client.disabled ? "disabled" : ""}>Disable temporary</button>
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
      <td>${modelClientsBadges(model.name, model.listedClients)}</td>
      <td>${modelClientsBadges(model.name, model.activeClients)}</td>
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
          ${modelClientsBadges(model, detail.listedClients)}
        </div>
        <div class="badge-row" style="margin-top:8px">
          ${badge("Active", detail.activeClients.length ? "good" : "warn")}
          ${modelClientsBadges(model, detail.activeClients)}
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
  pageSubtitle.textContent = "Keys accepted by the proxy token header, bearer auth, and token path. Query tokens work only on the status and tunnel endpoints.";

  patchContent(`
    ${state.newKey ? newKeyPanel(state.newKey, "New user key") : ""}
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
    ${state.newClientKey ? newKeyPanel(state.newClientKey, "New client key") : ""}
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

function newKeyPanel(key, label = "New user key") {
  return `
    <div class="new-key">
      <strong>${escapeHtml(label)}</strong>
      <code>${escapeHtml(key.key)}</code>
      <div class="actions">
        <button class="button secondary" type="button" data-action="copy-key" data-key="${escapeAttr(key.key)}">Copy</button>
      </div>
    </div>
  `;
}

function userKeysTable(keys) {
  const userKeyGroups = state.summary?.userKeyGroups || [];
  const groupsByKey = new Map(userKeyGroups.map((info) => [info.userKeyId, info]));

  const rows = keys.map((key) => {
    const keyGroups = groupsByKey.get(key.id);
    const hasGroups = keyGroups && keyGroups.groupIds.length > 0;
    const groupsCell = hasGroups
      ? `<div class="badge-row">${keyGroups.groupNames.map((name, index) =>
          `<a class="badge" href="#groups/${encodeURIComponent(keyGroups.groupIds[index])}">${escapeHtml(name)}</a>`
        ).join("")}</div>`
      : `<span class="cell-sub">No groups</span>`;
    const warning = hasGroups
      ? ""
      : `<button class="warn-button" type="button" data-action="toggle-help" data-owner-id="user-${escapeAttr(key.id)}"
          data-help="This user key has no groups yet, so it does not have access to any models. Assign it to a group to grant access."
          aria-label="No groups">!</button>`;

    return `
    <tr>
      <td>
        <div class="cell-main">
          <span>${escapeHtml(key.name)}</span>
          ${warning}
        </div>
        <div class="cell-sub">${escapeHtml(key.keyPrefix)}...</div>
      </td>
      <td>${formatDate(key.createdAtUtc)}</td>
      <td>${key.lastUsedUtc ? formatDate(key.lastUsedUtc) : "Never"}</td>
      <td>${groupsCell}</td>
      <td>
        <div class="actions">
          <button class="button secondary" data-action="manage-user-key-groups" data-key-id="${escapeAttr(key.id)}" data-key-name="${escapeAttr(key.name)}">Manage groups</button>
          <button class="button danger" data-action="delete-key" data-key-id="${escapeAttr(key.id)}">Delete</button>
        </div>
      </td>
    </tr>
  `;
  }).join("");

  return `
    <table>
      <thead>
        <tr>
          <th>Name</th>
          <th>Created</th>
          <th>Last used</th>
          <th>Groups</th>
          <th></th>
        </tr>
      </thead>
      <tbody>${rows}</tbody>
    </table>
  `;
}

function clientKeysTable(keys) {
  const clientKeyGroups = state.summary?.clientKeyGroups || [];
  const groupsByKey = new Map(clientKeyGroups.map((info) => [info.clientKeyId, info]));

  const rows = keys.map((key) => {
    const keyGroups = groupsByKey.get(key.id);
    const hasGroups = keyGroups && keyGroups.groupIds.length > 0;
    const groupsCell = hasGroups
      ? `<div class="badge-row">${keyGroups.groupNames.map((name, index) =>
          `<a class="badge" href="#groups/${encodeURIComponent(keyGroups.groupIds[index])}">${escapeHtml(name)}</a>`
        ).join("")}</div>`
      : `<span class="cell-sub">No groups</span>`;
    const warning = hasGroups
      ? ""
      : `<button class="warn-button" type="button" data-action="toggle-help" data-owner-id="client-${escapeAttr(key.id)}"
          data-help="This client key has no groups yet, so it is not yet accessible by any users in any group. Assign it to a group to grant access."
          aria-label="No groups">!</button>`;

    return `
    <tr>
      <td>
        <div class="cell-main">
          <span>${escapeHtml(key.name)}</span>
          ${warning}
        </div>
        <div class="cell-sub">${escapeHtml(key.keyPrefix)}...</div>
      </td>
      <td>${formatDate(key.createdAtUtc)}</td>
      <td>${key.lastUsedUtc ? formatDate(key.lastUsedUtc) : "Never"}</td>
      <td>${groupsCell}</td>
      <td>
        <div class="actions">
          <button class="button secondary" data-action="manage-client-key-groups" data-key-id="${escapeAttr(key.id)}" data-key-name="${escapeAttr(key.name)}">Manage groups</button>
          <button class="button danger" data-action="delete-client-key" data-key-id="${escapeAttr(key.id)}">Delete</button>
        </div>
      </td>
    </tr>
  `;
  }).join("");

  return `
    <table>
      <thead>
        <tr>
          <th>Name</th>
          <th>Created</th>
          <th>Last used</th>
          <th>Groups</th>
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
    const [group, clients, userKeyGroups, clientKeyGroups, billing, rules, payments, balance] = await Promise.all([
      api(`/groups/${encodeURIComponent(groupId)}`),
      api(`/groups/${encodeURIComponent(groupId)}/clients`),
      api("/user-keys/groups"),
      api("/client-keys/groups"),
      api(`/groups/${encodeURIComponent(groupId)}/billing`),
      api(`/groups/${encodeURIComponent(groupId)}/billing/rules`),
      api(`/groups/${encodeURIComponent(groupId)}/billing/payments`),
      api(`/groups/${encodeURIComponent(groupId)}/billing/balance`)
    ]);

    state.groupDetail = { group, clients, userKeyGroups, clientKeyGroups, billing, rules, payments, balance };
    renderGroupDetail();
  } catch (error) {
    patchContent(`<div class="panel"><div class="empty">${escapeHtml(error.message)}</div></div>`);
  }
}

function renderGroupDetail() {
  const { group, clients, userKeyGroups, clientKeyGroups, billing, rules, payments, balance } = state.groupDetail;
  const allUserKeys = state.summary?.userKeys || [];
  const allClientKeys = state.summary?.clientKeys || [];
  const availableModelNames = (state.summary?.models || []).map((model) => model.name);
  const assignedKeyIds = new Set(
    userKeyGroups
      .filter((akg) => akg.userKeyId && (akg.groupIds || []).includes(group.id))
      .map((akg) => akg.userKeyId)
  );
  const assignedClientKeyIds = new Set(
    clientKeyGroups
      .filter((akg) => akg.clientKeyId && (akg.groupIds || []).includes(group.id))
      .map((akg) => akg.clientKeyId)
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
        <h2>Add model</h2>
      </div>
      <div class="panel-body">
        <form class="form-row" data-form="add-model" data-group-id="${escapeAttr(group.id)}">
          <div class="field">
            ${renderFieldLabel("addModel", "Model", "An exact model name or a regex pattern is allowed. The models are served to all clients in the group.")}
            <input class="input" id="addModel" name="model" placeholder="bge-m3:latest" list="availableModels" required>
            ${availableModelNames.length ? `<datalist id="availableModels">${availableModelNames.map((name) => `<option value="${escapeAttr(name)}"></option>`).join("")}</datalist>` : ""}
          </div>
          <div class="field">
            ${renderFieldLabel("addModelKeepaliveInstances", "Min always loaded instances", "The minimum number of warm model instances that should stay loaded and ready so requests do not wait for a cold start. Set to 0 to keep none loaded between requests. Rules are combined per client, so several groups cooperate: 0 only unloads models no other rule wants warm.")}
            <input class="input" id="addModelKeepaliveInstances" name="keepaliveInstancesToKeepAlive" type="number" min="0" step="1" value="0">
          </div>
          <div class="field">
            ${renderFieldLabel("addModelKeepaliveMaxParallelism", "Max parallelism per client", "The maximum number of concurrent requests this client can handle at once. Higher values let one client absorb more traffic, but too many can overload the GPU.")}
            <input class="input" id="addModelKeepaliveMaxParallelism" name="keepaliveMaxParallelismPerClient" type="number" min="1" step="1" value="1">
          </div>
          <div class="field">
            ${renderFieldLabel("addModelKeepaliveHeadroom", "Parallelism headroom", "How much spare parallelism to leave unused so traffic spikes can be absorbed without saturating the GPU. A larger headroom makes routing more conservative. Set to 0 for no headroom.")}
            <input class="input" id="addModelKeepaliveHeadroom" name="keepaliveParallelismHeadroom" type="number" min="0" step="1" value="0">
          </div>
          <button class="button" type="submit">Add model</button>
        </form>
        <div class="cell-sub" style="margin-top:8px">The model is served to all clients in the group. A regex pattern is also allowed (e.g. <span class="code-inline">bge-m3.*</span>). Keepalive settings control warm instances and parallelism.</div>
      </div>
    </div>
    <div class="panel">
      <div class="panel-header">
        <h2>Models</h2>
        <span class="badge">${clients.length} total</span>
      </div>
      <div class="table-wrap">
        ${clients.length ? groupModelsTable(clients, group.id) : emptyState("No models have been added to this group.")}
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
        <h2>Client key assignments</h2>
      </div>
      <div class="table-wrap">
        ${allClientKeys.length ? clientKeyAssignmentTable(allClientKeys, group.id, assignedClientKeyIds) : emptyState("No client keys have been created.")}
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

function modelSelectorMatches(selector, model) {
  if (!selector || !model) {
    return false;
  }
  const s = selector.trim();
  const m = model.trim();
  if (s.toLowerCase() === m.toLowerCase()) {
    return true;
  }
  if (stripLatestTag(s).toLowerCase() === stripLatestTag(m).toLowerCase()) {
    return true;
  }
  try {
    return new RegExp(s, "i").test(m);
  } catch {
    return false;
  }
}

function stripLatestTag(model) {
  return model.toLowerCase().endsWith(":latest") ? model.slice(0, -7) : model;
}

function groupModelsTable(models, groupId) {
  const rows = models.map((model) => `
    <tr>
      <td>
        ${model.model
          ? `<div class="cell-main">${escapeHtml(model.model)}</div>`
          : `<div class="cell-sub">All models</div>`
        }
      </td>
      <td>${formatKeepalivePolicy(model.keepalivePolicy)}</td>
      <td>
        <button class="button danger" data-action="remove-client" data-group-id="${escapeAttr(groupId)}" data-member-id="${model.id}">Remove</button>
      </td>
    </tr>
  `).join("");

  return `
    <table>
      <thead>
        <tr>
          <th>Model</th>
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

function clientKeyAssignmentTable(clientKeys, groupId, assignedKeyIds) {
  const rows = clientKeys.map((key) => {
    const isAssigned = assignedKeyIds.has(key.id);
    return `
      <tr>
        <td>
          <div class="cell-main">${escapeHtml(key.name)}</div>
          <div class="cell-sub">${escapeHtml(key.keyPrefix)}...</div>
        </td>
        <td>
          <button class="button ${isAssigned ? "danger" : "secondary"}"
            data-action="toggle-client-key-assignment"
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
          <th>Client key</th>
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

async function toggleClientKeyAssignment(groupId, keyId, currentlyAssigned) {
  const clientKeyGroups = state.groupDetail?.clientKeyGroups || [];

  const keyGroups = clientKeyGroups.find((ckg) => ckg.clientKeyId === keyId);
  const currentGroupIds = keyGroups ? [...keyGroups.groupIds] : [];

  let newGroupIds;
  if (currentlyAssigned) {
    newGroupIds = currentGroupIds.filter((id) => id !== groupId);
  } else {
    newGroupIds = [...currentGroupIds, groupId];
  }

  await api(`/client-keys/${encodeURIComponent(keyId)}/groups`, {
    method: "PUT",
    body: { groupIds: newGroupIds }
  });

  setNotice(currentlyAssigned ? "Client key unassigned from group." : "Client key assigned to group.");
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

function warmthBadgeClass(value) {
  if (value < 0) {
    if (value >= -10) return "warm-green-blue";
    if (value >= -100) return "warm-light-blue";
    if (value >= -1000) return "warm-blue";
    return "warm-dark-blue";
  }
  if (value > 0) {
    if (value <= 10) return "warm-yellow";
    if (value <= 100) return "warm-orange";
    if (value <= 1000) return "warm-red";
    return "warm-dark-red";
  }
  return "";
}

function formatWarmth(value) {
  return value < 0 ? String(value) : `+${value}`;
}

function clientWarmth(client) {
  return client && Number.isInteger(client.warmth) ? client.warmth : 0;
}

function modelOverrideWarmth(client, model) {
  const override = (client?.modelWarmth || []).find((item) => item.model === model);
  return override && Number.isInteger(override.warmth) ? override.warmth : 0;
}

function effectiveWarmth(clientId, model) {
  const client = (state.summary?.clients || []).find((item) => item.id === clientId);
  return clientWarmth(client) + modelOverrideWarmth(client, model);
}

function warmthTip(clientId, model) {
  const client = (state.summary?.clients || []).find((item) => item.id === clientId);
  if (typeof model !== "string") {
    const base = clientWarmth(client);
    return `Warmth ${formatWarmth(base)} (base). Click to change.`;
  }
  const base = clientWarmth(client);
  const override = modelOverrideWarmth(client, model);
  const effective = base + override;
  const parts = [`base ${formatWarmth(base)}`, `model ${formatWarmth(override)}`, `effective ${formatWarmth(effective)}`];
  return `Warmth: ${parts.join(", ")}. Click to change.`;
}

function warmthModelBadge(clientId, model) {
  const value = effectiveWarmth(clientId, model);
  return `<span class="badge ${warmthBadgeClass(value)}" data-warmth-edit="model" data-client-id="${escapeAttr(clientId)}" data-model="${escapeAttr(model)}" data-warmth="${value}" title="${escapeAttr(warmthTip(clientId, model))}" aria-label="Edit warmth for ${escapeAttr(model)} on ${escapeAttr(clientId)}">${escapeHtml(model)}</span>`;
}

function modelClientsBadges(model, clients) {
  if (!clients || !clients.length) {
    return `<span class="cell-sub">None</span>`;
  }
  return `<div class="badge-row">${clients.map((clientId) => warmthModelBadge(clientId, model)).join("")}</div>`;
}

function warmthModelBadges(client, models) {
  if (!models || !models.length) {
    return `<span class="cell-sub">None</span>`;
  }
  return `<div class="badge-row">${models.map((model) => warmthModelBadge(client.id, model)).join("")}</div>`;
}

function clientWarmthBadge(client) {
  const value = clientWarmth(client);
  const color = warmthBadgeClass(value);
  return `<span class="badge ${color}" data-warmth-edit="client" data-client-id="${escapeAttr(client.id)}" data-warmth="${value}" title="${escapeAttr(warmthTip(client.id))}" aria-label="Edit base warmth for ${escapeAttr(client.id)}">🔥 ${formatWarmth(value)}</span>`;
}

async function editWarmth(target) {
  const isModel = target.warmthEdit === "model";
  const clientId = target.clientId;
  const current = Number(target.warmth || 0);
  const label = isModel
    ? `warmth for "${target.model}" on ${clientId}`
    : `base warmth for ${clientId}`;

  let value;
  try {
    value = await showWarmthModal({
      title: `Set ${label}`,
      current,
      hint: "Coldest models are unloaded first when demand goes back down. Warmer models are unloaded last. Warmth is calculated by adding client warmth and model warmth."
    });
  } catch {
    return;
  }

  const path = isModel
    ? `/clients/${encodeURIComponent(clientId)}/models/${encodeURIComponent(target.model)}/warmth`
    : `/clients/${encodeURIComponent(clientId)}/warmth`;

  await api(path, { method: "PUT", body: { warmth: value } });
  setNotice(`Updated ${label} to ${formatWarmth(value)}.`);
  await refresh();
}

function showWarmthModal({ title, current, hint }) {
  return new Promise((resolve, reject) => {
    const overlay = document.createElement("div");
    overlay.className = "modal-overlay";
    overlay.innerHTML = `
      <div class="modal-dialog">
        <h3>${escapeHtml(title)}</h3>
        <p class="field-label">${escapeHtml(hint)}</p>
        <div class="field">
          <span class="field-label">Warmth</span>
          <input type="number" id="warmth-value" class="input" value="${current}" step="1" autocomplete="off">
        </div>
        <div class="form-row modal-actions">
          <button class="button secondary" id="warmth-cancel" type="button">Cancel</button>
          <button class="button warning" id="warmth-ok" type="button">Save</button>
        </div>
      </div>
    `;

    document.body.appendChild(overlay);
    requestAnimationFrame(() => overlay.classList.add("visible"));

    const input = overlay.querySelector("#warmth-value");
    input.focus();
    input.select();

    function close() {
      overlay.remove();
      document.removeEventListener("keydown", onKeyDown);
    }

    function submit() {
      const raw = input.value.trim();
      const value = Number(raw);
      if (raw === "" || !Number.isInteger(value) || value < -2147483648 || value > 2147483647) {
        setNotice("Warmth must be an integer.", true);
        return;
      }
      close();
      resolve(value);
    }

    function onKeyDown(event) {
      if (event.key === "Escape") {
        close();
        reject(new Error("Cancelled"));
      } else if (event.key === "Enter") {
        submit();
      }
    }

    document.addEventListener("keydown", onKeyDown);

    overlay.querySelector("#warmth-ok").addEventListener("click", submit);
    overlay.querySelector("#warmth-cancel").addEventListener("click", () => {
      close();
      reject(new Error("Cancelled"));
    });

    overlay.addEventListener("click", (e) => {
      if (e.target === overlay) {
        close();
        reject(new Error("Cancelled"));
      }
    });
  });
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
  var text = "";
  if (client.disabledManually) {
    text = "Until enabled manually";
  } else if (client.disabledUntilUtc) {
    text = `Until ${formatDate(client.disabledUntilUtc)}`;
  } else if (isScheduled(client)) {
    text = `Scheduled from ${formatDate(client.disabledFromUtc)}`;
  } else {
    text = "Disabled";
  }
  if (client.disabledReason) {
    text += ` — ${escapeHtml(client.disabledReason)}`;
  }
  return text;
}

function isScheduled(client) {
  return client.disabledFromUtc && new Date(client.disabledFromUtc) > new Date();
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

function showUserKeyGroupsModal(userKeyId, keyName) {
  return new Promise((resolve, reject) => {
    const groups = state.summary?.groups || [];
    const userKeyGroups = state.summary?.userKeyGroups || [];
    const current = userKeyGroups.find((info) => info.userKeyId === userKeyId);
    const assigned = new Set(current ? current.groupIds : []);

    const overlay = document.createElement("div");
    overlay.className = "modal-overlay";
    overlay.innerHTML = `
      <div class="modal-dialog">
        <h3>Manage groups for &quot;${escapeHtml(keyName || userKeyId)}&quot;</h3>
        <p class="field-label">User keys only get access to the models of the groups they are assigned to.</p>
        ${groups.length
          ? `<div class="key-group-list">${groups.map((group) => `
              <label class="key-group-item">
                <input type="checkbox" value="${escapeAttr(group.id)}" ${assigned.has(group.id) ? "checked" : ""}>
                <span>${escapeHtml(group.name)}</span>
              </label>
            `).join("")}</div>`
          : `<div class="empty">No groups have been created yet.</div>`}
        <div class="form-row modal-actions">
          <button class="button secondary" id="ug-cancel" type="button">Cancel</button>
          <button class="button" id="ug-save" type="button">Save</button>
        </div>
      </div>
    `;

    document.body.appendChild(overlay);
    requestAnimationFrame(() => overlay.classList.add("visible"));

    function close() {
      overlay.remove();
      document.removeEventListener("keydown", onKeyDown);
    }

    function onKeyDown(event) {
      if (event.key === "Escape") {
        reject(new Error("Cancelled"));
        close();
      }
    }

    document.addEventListener("keydown", onKeyDown);

    overlay.querySelector("#ug-cancel").addEventListener("click", () => {
      reject(new Error("Cancelled"));
      close();
    });

    overlay.querySelector("#ug-save").addEventListener("click", () => {
      const groupIds = [...overlay.querySelectorAll('input[type="checkbox"]:checked')].map((box) => box.value);
      close();
      resolve({ groupIds });
    });

    overlay.addEventListener("click", (e) => {
      if (e.target === overlay) {
        reject(new Error("Cancelled"));
        close();
      }
    });
  });
}

function showClientKeyGroupsModal(clientKeyId, keyName) {
  return new Promise((resolve, reject) => {
    const groups = state.summary?.groups || [];
    const clientKeyGroups = state.summary?.clientKeyGroups || [];
    const current = clientKeyGroups.find((info) => info.clientKeyId === clientKeyId);
    const assigned = new Set(current ? current.groupIds : []);

    const overlay = document.createElement("div");
    overlay.className = "modal-overlay";
    overlay.innerHTML = `
      <div class="modal-dialog">
        <h3>Manage groups for &quot;${escapeHtml(keyName || clientKeyId)}&quot;</h3>
        <p class="field-label">Client keys only get access to the models of the groups they are assigned to.</p>
        ${groups.length
          ? `<div class="key-group-list">${groups.map((group) => `
              <label class="key-group-item">
                <input type="checkbox" value="${escapeAttr(group.id)}" ${assigned.has(group.id) ? "checked" : ""}>
                <span>${escapeHtml(group.name)}</span>
              </label>
            `).join("")}</div>`
          : `<div class="empty">No groups have been created yet.</div>`}
        <div class="form-row modal-actions">
          <button class="button secondary" id="cg-cancel" type="button">Cancel</button>
          <button class="button" id="cg-save" type="button">Save</button>
        </div>
      </div>
    `;

    document.body.appendChild(overlay);
    requestAnimationFrame(() => overlay.classList.add("visible"));

    function close() {
      overlay.remove();
      document.removeEventListener("keydown", onKeyDown);
    }

    function onKeyDown(event) {
      if (event.key === "Escape") {
        reject(new Error("Cancelled"));
        close();
      }
    }

    document.addEventListener("keydown", onKeyDown);

    overlay.querySelector("#cg-cancel").addEventListener("click", () => {
      reject(new Error("Cancelled"));
      close();
    });

    overlay.querySelector("#cg-save").addEventListener("click", () => {
      const groupIds = [...overlay.querySelectorAll('input[type="checkbox"]:checked')].map((box) => box.value);
      close();
      resolve({ groupIds });
    });

    overlay.addEventListener("click", (e) => {
      if (e.target === overlay) {
        reject(new Error("Cancelled"));
        close();
      }
    });
  });
}

function showDisableModal(clientId) {
  return new Promise((resolve, reject) => {
    const overlay = document.createElement("div");
    overlay.className = "modal-overlay";
    overlay.innerHTML = `
      <div class="modal-dialog">
        <h3>Disable client &quot;${escapeHtml(clientId)}&quot;</h3>
        <div class="field">
          <span class="field-label">When to disable</span>
          <div class="radio-row">
            <label><input type="radio" name="d-when" value="now" checked> Now</label>
            <label><input type="radio" name="d-when" value="later"> Later</label>
          </div>
          <input type="datetime-local" id="d-from" class="input" disabled>
        </div>
        <div class="field">
          <span class="field-label">For how long</span>
          <div class="radio-row">
            <label><input type="radio" name="d-for" value="timespan" checked> Timespan</label>
            <label><input type="radio" name="d-for" value="until"> Until</label>
          </div>
          <div id="d-timespan-group" class="field-row">
            <input type="number" id="d-duration" class="input" value="1" min="1" style="width:100px">
            <select id="d-unit" class="select" style="width:auto">
              <option value="1">minute(s)</option>
              <option value="60" selected>hour(s)</option>
              <option value="1440">day(s)</option>
            </select>
          </div>
          <input type="datetime-local" id="d-until" class="input" style="display:none" disabled>
        </div>
        <div class="field">
          <label for="d-reason">Reason (optional)</label>
          <input type="text" id="d-reason" class="input" placeholder="e.g. maintenance">
        </div>
        <div class="form-row modal-actions">
          <button class="button secondary" id="d-cancel" type="button">Cancel</button>
          <button class="button warning" id="d-confirm" type="button">Disable</button>
        </div>
      </div>
    `;

    document.body.appendChild(overlay);
    requestAnimationFrame(() => overlay.classList.add("visible"));

    const whenRadios = overlay.querySelectorAll('input[name="d-when"]');
    const forRadios = overlay.querySelectorAll('input[name="d-for"]');
    const fromInput = overlay.querySelector("#d-from");
    const timespanGroup = overlay.querySelector("#d-timespan-group");
    const untilInput = overlay.querySelector("#d-until");

    function updateWhen() {
      const val = overlay.querySelector('input[name="d-when"]:checked').value;
      fromInput.style.display = val === "later" ? "" : "none";
      fromInput.disabled = val !== "later";
      if (val === "now") fromInput.value = "";
    }

    function updateFor() {
      const val = overlay.querySelector('input[name="d-for"]:checked').value;
      timespanGroup.style.display = val === "timespan" ? "flex" : "none";
      untilInput.style.display = val === "until" ? "" : "none";
      untilInput.disabled = val !== "until";
      if (val === "timespan") untilInput.value = "";
    }

    whenRadios.forEach(r => r.addEventListener("change", updateWhen));
    forRadios.forEach(r => r.addEventListener("change", updateFor));
    updateFor();

    overlay.querySelector("#d-confirm").addEventListener("click", () => {
      const when = overlay.querySelector('input[name="d-when"]:checked').value;
      const forVal = overlay.querySelector('input[name="d-for"]:checked').value;
      const reason = overlay.querySelector("#d-reason").value.trim() || null;

      if (when === "later" && !fromInput.value) {
        setNotice("Please select a date and time for the disable.", true);
        return;
      }
      if (forVal === "until" && !untilInput.value) {
        setNotice("Please select a date and time for the end.", true);
        return;
      }
      if (forVal === "timespan") {
        const val = parseInt(overlay.querySelector("#d-duration").value);
        if (!val || val < 1) {
          setNotice("Please enter a valid duration.", true);
          return;
        }
      }

      const body = { reason };

      if (when === "later") {
        body.startAtUtc = new Date(fromInput.value).toISOString();
      }

      if (forVal === "timespan") {
        const val = parseInt(overlay.querySelector("#d-duration").value) || 1;
        const unit = parseInt(overlay.querySelector("#d-unit").value);
        body.durationMinutes = val * unit;
      } else {
        body.untilUtc = new Date(untilInput.value).toISOString();
      }

      overlay.remove();
      resolve(body);
    });

    overlay.querySelector("#d-cancel").addEventListener("click", () => {
      overlay.remove();
      reject(new Error("Cancelled"));
    });

    overlay.addEventListener("click", (e) => {
      if (e.target === overlay) {
        overlay.remove();
        reject(new Error("Cancelled"));
      }
    });
  });
}

function showConfirmModal(message, confirmLabel = "Confirm") {
  return new Promise((resolve, reject) => {
    const overlay = document.createElement("div");
    overlay.className = "modal-overlay";
    overlay.innerHTML = `
      <div class="modal-dialog">
        <h3>Confirm</h3>
        <p class="field-label">${escapeHtml(message)}</p>
        <div class="form-row modal-actions">
          <button class="button secondary" id="confirm-cancel" type="button">Cancel</button>
          <button class="button warning" id="confirm-ok" type="button">${escapeHtml(confirmLabel)}</button>
        </div>
      </div>
    `;

    document.body.appendChild(overlay);
    requestAnimationFrame(() => overlay.classList.add("visible"));

    function close() {
      overlay.remove();
      document.removeEventListener("keydown", onKeyDown);
    }

    function onKeyDown(event) {
      if (event.key === "Escape") {
        close();
        reject(new Error("Cancelled"));
      }
    }

    document.addEventListener("keydown", onKeyDown);

    overlay.querySelector("#confirm-cancel").addEventListener("click", () => {
      close();
      reject(new Error("Cancelled"));
    });

    overlay.querySelector("#confirm-ok").addEventListener("click", () => {
      close();
      resolve(true);
    });

    overlay.addEventListener("click", (e) => {
      if (e.target === overlay) {
        close();
        reject(new Error("Cancelled"));
      }
    });
  });
}

boot();
