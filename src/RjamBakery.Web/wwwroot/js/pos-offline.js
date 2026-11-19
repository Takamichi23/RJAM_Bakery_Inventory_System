(() => {
    const DB_NAME = 'rjam_pos_offline_v1';
    const DB_VERSION = 1;
    const META_STORE = 'meta';
    const QUEUE_STORE = 'queue';

    const PAYMENT_METHODS = [
        { value: 1, label: 'Cash' },
        { value: 2, label: 'GCash' },
        { value: 3, label: 'Card' },
        { value: 4, label: 'Bank Transfer' }
    ];

    let dbPromise = null;
    let dotNetRef = null;
    let isSyncing = false;
    let syncTimer = null;
    let reconnectObserver = null;
    let overlayHost = null;
    let activeController = null;

    function roundMoney(v) {
        const n = Number(v);
        if (!Number.isFinite(n)) return 0;
        return Math.round(n * 100) / 100;
    }

    function safeStatusClass(status) {
        const normalized = String(status || '').toLowerCase();
        return normalized === 'pending' || normalized === 'conflict' || normalized === 'synced'
            ? normalized
            : 'pending';
    }

    function esc(str) {
        return String(str ?? '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }

    function formatMoney(currency, amount) {
        const num = Number(amount) || 0;
        return `${currency}${num.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}`;
    }

    function formatQty(qty) {
        const n = Number(qty) || 0;
        return Number.isInteger(n) ? String(n) : n.toFixed(2).replace(/\.?0+$/, '');
    }

    function generateIdempotencyKey() {
        const now = new Date();
        const y = now.getFullYear();
        const m = String(now.getMonth() + 1).padStart(2, '0');
        const d = String(now.getDate()).padStart(2, '0');
        const rand = (crypto?.randomUUID?.() || `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 10)}`)
            .replace(/-/g, '')
            .slice(0, 12)
            .toUpperCase();
        return `OFF-${y}${m}${d}-${rand}`;
    }

    function openDb() {
        if (dbPromise) return dbPromise;
        dbPromise = new Promise((resolve, reject) => {
            if (!('indexedDB' in window)) {
                reject(new Error('IndexedDB unavailable'));
                return;
            }
            const req = indexedDB.open(DB_NAME, DB_VERSION);
            req.onupgradeneeded = () => {
                const db = req.result;
                if (!db.objectStoreNames.contains(META_STORE)) {
                    db.createObjectStore(META_STORE, { keyPath: 'key' });
                }
                if (!db.objectStoreNames.contains(QUEUE_STORE)) {
                    db.createObjectStore(QUEUE_STORE, { keyPath: 'clientIdempotencyKey' });
                }
            };
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });
        return dbPromise;
    }

    async function dbGetMeta(key) {
        try {
            const db = await openDb();
            return await new Promise((resolve, reject) => {
                const tx = db.transaction(META_STORE, 'readonly');
                const req = tx.objectStore(META_STORE).get(key);
                req.onsuccess = () => resolve(req.result || null);
                req.onerror = () => reject(req.error);
            });
        } catch {
            try {
                const raw = localStorage.getItem(`rjam_meta_${key}`);
                return raw ? JSON.parse(raw) : null;
            } catch {
                return null;
            }
        }
    }

    async function dbPutMeta(record) {
        try {
            const db = await openDb();
            await new Promise((resolve, reject) => {
                const tx = db.transaction(META_STORE, 'readwrite');
                tx.objectStore(META_STORE).put(record);
                tx.oncomplete = () => resolve();
                tx.onerror = () => reject(tx.error);
            });
        } catch {
            try { localStorage.setItem(`rjam_meta_${record.key}`, JSON.stringify(record)); } catch { }
        }
    }

    async function dbGetAllQueue() {
        try {
            const db = await openDb();
            const items = await new Promise((resolve, reject) => {
                const tx = db.transaction(QUEUE_STORE, 'readonly');
                const req = tx.objectStore(QUEUE_STORE).getAll();
                req.onsuccess = () => resolve(req.result || []);
                req.onerror = () => reject(req.error);
            });
            return items.sort((a, b) => (a.createdAt || '').localeCompare(b.createdAt || ''));
        } catch {
            try {
                const raw = localStorage.getItem('rjam_pos_queue');
                return raw ? JSON.parse(raw) : [];
            } catch {
                return [];
            }
        }
    }

    async function dbPutQueueItem(item) {
        try {
            const db = await openDb();
            await new Promise((resolve, reject) => {
                const tx = db.transaction(QUEUE_STORE, 'readwrite');
                tx.objectStore(QUEUE_STORE).put(item);
                tx.oncomplete = () => resolve();
                tx.onerror = () => reject(tx.error);
            });
        } catch {
            const all = await dbGetAllQueue();
            const idx = all.findIndex((x) => x.clientIdempotencyKey === item.clientIdempotencyKey);
            if (idx >= 0) all[idx] = item; else all.push(item);
            try { localStorage.setItem('rjam_pos_queue', JSON.stringify(all)); } catch { }
        }
    }

    async function dbDeleteQueueItem(key) {
        try {
            const db = await openDb();
            await new Promise((resolve, reject) => {
                const tx = db.transaction(QUEUE_STORE, 'readwrite');
                tx.objectStore(QUEUE_STORE).delete(key);
                tx.oncomplete = () => resolve();
                tx.onerror = () => reject(tx.error);
            });
        } catch {
            const all = (await dbGetAllQueue()).filter((x) => x.clientIdempotencyKey !== key);
            try { localStorage.setItem('rjam_pos_queue', JSON.stringify(all)); } catch { }
        }
    }

    async function getSummaryState(lastSyncedCount = 0, lastMessage = '') {
        const [catalog, queue] = await Promise.all([dbGetMeta('catalog'), dbGetAllQueue()]);
        const pendingCount = queue.filter((q) => q.status === 'Pending').length;
        const conflictCount = queue.filter((q) => q.status === 'Conflict').length;
        const syncedCount = queue.filter((q) => q.status === 'Synced').length;
        return {
            isOnline: navigator.onLine !== false,
            pendingCount,
            conflictCount,
            syncedCount,
            cachedItemCount: catalog?.items?.length || 0,
            cachedAt: catalog?.cachedAt || null,
            lastSyncedCount,
            lastMessage
        };
    }

    async function notifyListeners(lastSyncedCount = 0, lastMessage = '') {
        const state = await getSummaryState(lastSyncedCount, lastMessage);
        if (dotNetRef) {
            try {
                await dotNetRef.invokeMethodAsync('OnOfflineStateChanged', state);
            } catch {
                // Blazor circuit disconnected; ignore safely
            }
        }
        if (activeController) {
            await activeController.refresh(lastMessage);
        }
        return state;
    }

    async function saveCatalogSnapshot(snapshot) {
        if (!snapshot) return;
        const record = {
            key: 'catalog',
            bakeryName: snapshot.bakeryName || 'RJAM Bakery',
            currencySymbol: snapshot.currencySymbol || '₱',
            categories: Array.isArray(snapshot.categories) ? snapshot.categories : [],
            items: Array.isArray(snapshot.items) ? snapshot.items : [],
            cachedAt: new Date().toISOString()
        };
        await dbPutMeta(record);
        return await getSummaryState();
    }

    async function refreshCatalogFromServer() {
        try {
            const resp = await fetch('/api/pos/catalog', {
                method: 'GET',
                credentials: 'same-origin',
                headers: {
                    'Accept': 'application/json',
                    'X-Requested-With': 'RJAM-Offline-POS'
                },
                cache: 'no-store'
            });
            if (!resp.ok) return false;
            const items = await resp.json();
            const existing = (await dbGetMeta('catalog')) || {};
            const catMap = new Map();
            for (const c of (existing.categories || [])) catMap.set(c.id, c.name);
            for (const it of items) {
                if (it.categoryId && it.categoryName) catMap.set(it.categoryId, it.categoryName);
            }
            await saveCatalogSnapshot({
                bakeryName: existing.bakeryName || 'RJAM Bakery',
                currencySymbol: existing.currencySymbol || '₱',
                categories: Array.from(catMap.entries()).map(([id, name]) => ({ id, name })),
                items
            });
            return true;
        } catch {
            return false;
        }
    }

    async function enqueueSale(saleDraft) {
        const key = saleDraft.clientIdempotencyKey || generateIdempotencyKey();
        const pm = PAYMENT_METHODS.find((m) => m.value === Number(saleDraft.paymentMethod)) || PAYMENT_METHODS[0];
        const subTotal = roundMoney(saleDraft.items.reduce((acc, l) => acc + (Number(l.unitPrice) * Number(l.quantity)), 0));
        const discount = Math.min(subTotal, Math.max(0, roundMoney(saleDraft.discountAmount || 0)));
        const total = Math.max(0, roundMoney(subTotal - discount));
        const tendered = roundMoney(saleDraft.amountTendered ?? total);
        const change = Math.max(0, roundMoney(tendered - total));

        const record = {
            clientIdempotencyKey: key,
            offlineReceiptNo: key,
            createdAt: new Date().toISOString(),
            paymentMethod: pm.value,
            paymentMethodLabel: pm.label,
            discountAmount: discount,
            amountTendered: tendered,
            subTotal,
            totalAmount: total,
            changeAmount: change,
            items: saleDraft.items.map((i) => ({
                productId: Number(i.productId),
                name: i.name,
                unitPrice: roundMoney(i.unitPrice),
                quantity: Number(i.quantity),
                unitCode: i.unitCode || 'pcs'
            })),
            status: 'Pending',
            errorMessage: null,
            syncedReceiptNumber: null,
            syncedAt: null
        };

        await dbPutQueueItem(record);
        await notifyListeners(0, '');
        return record;
    }

    async function syncNow() {
        if (isSyncing) {
            return await getSummaryState();
        }
        isSyncing = true;
        let syncedNow = 0;
        let authRequired = false;
        let lastMessage = '';

        try {
            const queue = await dbGetAllQueue();
            const pending = queue.filter((q) => q.status === 'Pending');
            if (pending.length === 0) {
                return await getSummaryState(0, '');
            }

            for (const entry of pending) {
                const payload = {
                    clientIdempotencyKey: entry.clientIdempotencyKey,
                    paymentMethod: entry.paymentMethod,
                    discountAmount: entry.discountAmount,
                    amountTendered: entry.amountTendered,
                    items: entry.items.map((i) => ({
                        productId: i.productId,
                        quantity: i.quantity
                    }))
                };

                let resp;
                try {
                    resp = await fetch('/api/pos/checkout', {
                        method: 'POST',
                        credentials: 'same-origin',
                        headers: {
                            'Content-Type': 'application/json',
                            'Accept': 'application/json',
                            'X-Requested-With': 'RJAM-Offline-POS'
                        },
                        body: JSON.stringify(payload)
                    });
                } catch {
                    // Server unreachable (offline) - keep Pending and exit loop cleanly
                    lastMessage = 'Still offline. Queued sales are safely stored on this device.';
                    break;
                }

                if (resp.ok) {
                    let receiptNum = null;
                    try {
                        const data = await resp.json();
                        receiptNum = data?.transaction?.receiptNumber || null;
                    } catch { }
                    entry.status = 'Synced';
                    entry.errorMessage = null;
                    entry.syncedReceiptNumber = receiptNum;
                    entry.syncedAt = new Date().toISOString();
                    await dbPutQueueItem(entry);
                    syncedNow++;
                    continue;
                }

                if (resp.status === 401 || resp.status === 403) {
                    authRequired = true;
                    lastMessage = 'Sign in as Cashier or Owner to sync pending offline sales.';
                    break;
                }

                let problem = null;
                try { problem = await resp.json(); } catch { }
                const code = problem?.title || '';
                const detail = problem?.detail || `Server returned HTTP ${resp.status}`;

                if (resp.status === 409 && code === 'DUPLICATE_TRANSACTION') {
                    entry.status = 'Synced';
                    entry.errorMessage = null;
                    entry.syncedAt = new Date().toISOString();
                    await dbPutQueueItem(entry);
                    syncedNow++;
                    continue;
                }

                if (resp.status === 409 && code === 'CONCURRENCY_CONFLICT') {
                    // Leave Pending for automatic retry on next cycle
                    lastMessage = 'Store stock busy; will automatically retry remaining sales.';
                    break;
                }

                if (resp.status >= 400 && resp.status < 500) {
                    entry.status = 'Conflict';
                    entry.errorMessage = detail;
                    await dbPutQueueItem(entry);
                    continue;
                }

                // 5xx transient server failure -> keep Pending
                lastMessage = 'Server error while syncing; queued sales will retry automatically.';
                break;
            }

            if (syncedNow > 0) {
                await refreshCatalogFromServer();
                lastMessage = `Synced ${syncedNow} offline sale${syncedNow === 1 ? '' : 's'} to server.`;
            }
        } finally {
            isSyncing = false;
        }

        const state = await notifyListeners(syncedNow, lastMessage);
        return { ...state, authRequired };
    }

    async function retryConflict(key) {
        const all = await dbGetAllQueue();
        const found = all.find((x) => x.clientIdempotencyKey === key);
        if (!found) return;
        found.status = 'Pending';
        found.errorMessage = null;
        await dbPutQueueItem(found);
        return await syncNow();
    }

    async function removeQueueItem(key) {
        await dbDeleteQueueItem(key);
        return await notifyListeners(0, '');
    }

    async function clearSynced() {
        const all = await dbGetAllQueue();
        for (const item of all.filter((x) => x.status === 'Synced')) {
            await dbDeleteQueueItem(item.clientIdempotencyKey);
        }
        return await notifyListeners(0, '');
    }

    function computePendingReservedByProduct(queue) {
        const map = new Map();
        for (const q of queue) {
            if (q.status !== 'Pending') continue;
            for (const line of (q.items || [])) {
                map.set(line.productId, (map.get(line.productId) || 0) + Number(line.quantity));
            }
        }
        return map;
    }

    function createTerminalController(container, { standalone = false, initialTab = 'pos', onClose = null } = {}) {
        const state = {
            catalog: { bakeryName: 'RJAM Bakery', currencySymbol: '₱', categories: [], items: [], cachedAt: null },
            queue: [],
            search: '',
            categoryId: null,
            cart: [],
            discount: 0,
            paymentMethod: 1,
            tendered: 0,
            activeTab: initialTab,
            receipt: null,
            bannerMessage: ''
        };

        async function loadLocal() {
            const [cat, q] = await Promise.all([dbGetMeta('catalog'), dbGetAllQueue()]);
            if (cat) state.catalog = cat;
            state.queue = q;
        }

        function cartSubTotal() {
            return roundMoney(state.cart.reduce((s, c) => s + c.unitPrice * c.quantity, 0));
        }

        function cartTotal() {
            const sub = cartSubTotal();
            const disc = Math.min(sub, Math.max(0, roundMoney(state.discount)));
            return Math.max(0, roundMoney(sub - disc));
        }

        function render() {
            const online = navigator.onLine !== false;
            const cur = state.catalog.currencySymbol || '₱';
            const pendingCount = state.queue.filter((q) => q.status === 'Pending').length;
            const conflictCount = state.queue.filter((q) => q.status === 'Conflict').length;
            const reservedMap = computePendingReservedByProduct(state.queue);
            const term = state.search.trim().toLowerCase();

            const filteredItems = (state.catalog.items || []).filter((item) => {
                if (state.categoryId !== null && item.categoryId !== state.categoryId) return false;
                if (term && !`${item.name} ${item.sku}`.toLowerCase().includes(term)) return false;
                return true;
            });

            const subTotal = cartSubTotal();
            const total = cartTotal();
            const change = Math.max(0, roundMoney(state.tendered - total));

            container.innerHTML = `
                <div class="rjam-offpos-shell">
                    <header class="rjam-offpos-topbar">
                        <div class="rjam-offpos-brand">
                            <img src="/images/rjam-logo-light.png" alt="RJAM Bakery" style="height:34px; width:auto;" />
                            <span class="rjam-offpos-pill ${online ? 'is-online' : 'is-offline'}">
                                ${online ? 'Online · Auto-Sync Active' : 'Offline Mode · Queuing Locally'}
                            </span>
                        </div>
                        <div class="rjam-offpos-actions">
                            <button type="button" class="rjam-off-btn ${state.activeTab === 'pos' ? 'is-active' : ''}" data-action="tab-pos">
                                POS Terminal
                            </button>
                            <button type="button" class="rjam-off-btn ${state.activeTab === 'queue' ? 'is-active' : ''}" data-action="tab-queue">
                                Sync Queue (${pendingCount + conflictCount})
                            </button>
                            <button type="button" class="rjam-off-btn is-primary" data-action="sync-now" ${isSyncing ? 'disabled' : ''}>
                                ${isSyncing ? 'Syncing...' : `Sync Now${pendingCount > 0 ? ` (${pendingCount})` : ''}`}
                            </button>
                            ${standalone
                                ? `<a href="/pos" class="rjam-off-btn">Live POS</a>`
                                : `<button type="button" class="rjam-off-btn" data-action="close-overlay">Return to Live POS</button>`}
                        </div>
                    </header>

                    <main class="rjam-offpos-body">
                        ${state.bannerMessage ? `<div class="rjam-off-banner">${esc(state.bannerMessage)}</div>` : ''}

                        ${state.activeTab === 'queue' ? renderQueueView(cur) : `
                            <div class="rjam-offpos-grid">
                                <section class="rjam-offpos-catalog">
                                    <div class="rjam-off-card">
                                        <div class="rjam-off-row-between">
                                            <div>
                                                <h2 class="rjam-off-h2">Offline-Ready Point of Sale</h2>
                                                <div class="rjam-off-muted">
                                                    ${state.catalog.cachedAt
                                                        ? `Catalog cached ${esc(new Date(state.catalog.cachedAt).toLocaleString())}`
                                                        : 'Open Live POS once while online to cache products on this device.'}
                                                </div>
                                            </div>
                                            ${state.cart.length > 0 ? `<a href="#rjam-off-cart" class="rjam-off-btn is-primary rjam-off-mobile-jump">Order (${state.cart.length} · ${esc(formatMoney(cur, total))})</a>` : ''}
                                        </div>

                                        <input type="text" class="auth-input" style="margin-top:12px;" placeholder="Search product name or SKU..." value="${esc(state.search)}" data-field="search" />

                                        <div class="rjam-off-chips">
                                            <button type="button" class="rjam-off-chip ${state.categoryId === null ? 'is-selected' : ''}" data-cat="all">All Items</button>
                                            ${(state.catalog.categories || []).map((c) => `
                                                <button type="button" class="rjam-off-chip ${state.categoryId === c.id ? 'is-selected' : ''}" data-cat="${c.id}">${esc(c.name)}</button>
                                            `).join('')}
                                        </div>
                                    </div>

                                    ${filteredItems.length === 0 ? `
                                        <div class="rjam-off-card rjam-off-empty">
                                            <strong>No cached bakery products found</strong>
                                            <div class="rjam-off-muted">Try clearing your filter, or open Live POS while connected to refresh store stock.</div>
                                        </div>
                                    ` : `
                                        <div class="rjam-off-cards">
                                            ${filteredItems.map((item) => {
                                                const pendingQty = reservedMap.get(item.productId) || 0;
                                                const effectiveStock = Math.max(0, roundMoney((Number(item.availableStoreStock) || 0) - pendingQty));
                                                const inCart = state.cart.find((c) => c.productId === item.productId)?.quantity || 0;
                                                const remaining = effectiveStock - inCart;
                                                const isOut = effectiveStock <= 0;
                                                return `
                                                    <div class="rjam-off-prod">
                                                        <div>
                                                            <div class="rjam-off-prod-top">
                                                                <span class="rjam-off-stock ${isOut ? 'is-out' : effectiveStock <= (item.minStockLevel || 0) ? 'is-low' : 'is-ok'}">
                                                                    ${esc(formatQty(effectiveStock))} ${esc(item.unitCode || 'pcs')}
                                                                </span>
                                                            </div>
                                                            <div class="rjam-off-prod-name">${esc(item.name)}</div>
                                                            <div class="rjam-off-muted" style="font-size:0.78rem;">${esc(item.description || item.sku)}</div>
                                                        </div>
                                                        <div class="rjam-off-prod-foot">
                                                            <strong class="tabular-nums">${esc(formatMoney(cur, item.sellingPrice))}</strong>
                                                            <button type="button" class="rjam-off-btn is-outline" data-add="${item.productId}" ${(isOut || remaining <= 0) ? 'disabled' : ''}>
                                                                ${isOut ? 'Sold Out' : 'Add to cart'}
                                                            </button>
                                                        </div>
                                                    </div>
                                                `;
                                            }).join('')}
                                        </div>
                                    `}
                                </section>

                                <aside class="rjam-offpos-cart" id="rjam-off-cart">
                                    <div class="rjam-off-card">
                                        <div class="rjam-off-row-between" style="margin-bottom:12px;">
                                            <h3 class="rjam-off-h3">Current Order</h3>
                                            ${state.cart.length > 0 ? `<button type="button" class="rjam-off-link-danger" data-action="clear-cart">Clear</button>` : ''}
                                        </div>

                                        ${state.cart.length === 0 ? `
                                            <div class="rjam-off-cart-empty">
                                                Select bakery items to start offline checkout.
                                            </div>
                                        ` : `
                                            <div class="rjam-off-cart-lines">
                                                ${state.cart.map((line) => `
                                                    <div class="rjam-off-cart-line">
                                                        <div class="rjam-off-row-between">
                                                            <strong>${esc(line.name)}</strong>
                                                            <button type="button" class="rjam-off-link-danger" data-remove="${line.productId}" aria-label="Remove">×</button>
                                                        </div>
                                                        <div class="rjam-off-row-between" style="margin-top:6px;">
                                                            <div class="rjam-off-qty">
                                                                <button type="button" class="rjam-off-qty-btn" data-dec="${line.productId}">-</button>
                                                                <span class="tabular-nums">${esc(formatQty(line.quantity))}</span>
                                                                <button type="button" class="rjam-off-qty-btn" data-inc="${line.productId}" ${line.quantity >= line.maxStock ? 'disabled' : ''}>+</button>
                                                            </div>
                                                            <strong class="tabular-nums">${esc(formatMoney(cur, line.unitPrice * line.quantity))}</strong>
                                                        </div>
                                                    </div>
                                                `).join('')}
                                            </div>

                                            <div class="rjam-off-totals">
                                                <div class="rjam-off-row-between">
                                                    <span class="rjam-off-muted">Subtotal</span>
                                                    <span class="tabular-nums">${esc(formatMoney(cur, subTotal))}</span>
                                                </div>

                                                <label class="auth-label" style="margin-top:10px;">Discount (${esc(cur)})</label>
                                                <input type="number" min="0" max="${subTotal}" step="0.01" class="auth-input" value="${state.discount}" data-field="discount" />

                                                <div class="rjam-off-row-between rjam-off-total-row">
                                                    <strong>Total</strong>
                                                    <strong class="tabular-nums">${esc(formatMoney(cur, total))}</strong>
                                                </div>

                                                <label class="auth-label">Payment Method</label>
                                                <select class="auth-input" data-field="paymentMethod">
                                                    ${PAYMENT_METHODS.map((m) => `<option value="${m.value}" ${state.paymentMethod === m.value ? 'selected' : ''}>${esc(m.label)}</option>`).join('')}
                                                </select>

                                                <label class="auth-label" style="margin-top:10px;">Amount Tendered (${esc(cur)})</label>
                                                <input type="number" min="0" step="0.01" class="auth-input" value="${state.tendered}" data-field="tendered" />

                                                <div class="rjam-off-chips" style="margin-top:8px;">
                                                    <button type="button" class="rjam-off-chip" data-tender="${total}">Exact</button>
                                                    <button type="button" class="rjam-off-chip" data-tender="${roundMoney(total + 50)}">+50</button>
                                                    <button type="button" class="rjam-off-chip" data-tender="${roundMoney(total + 100)}">+100</button>
                                                    <button type="button" class="rjam-off-chip" data-tender="${roundMoney(total + 500)}">+500</button>
                                                </div>

                                                <div class="rjam-off-row-between" style="margin:12px 0;">
                                                    <span class="rjam-off-muted">Change</span>
                                                    <strong class="tabular-nums" style="color:${state.tendered < total ? '#B84A39' : '#3E7B5A'};">
                                                        ${esc(formatMoney(cur, change))}
                                                    </strong>
                                                </div>

                                                <button type="button" class="auth-btn" data-action="checkout" ${state.tendered < total ? 'disabled' : ''}>
                                                    ${online ? 'Save & Sync Sale' : 'Queue Offline Sale & Print Receipt'}
                                                </button>
                                            </div>
                                        `}
                                    </div>
                                </aside>
                            </div>
                        `}
                    </main>

                    ${state.receipt ? renderReceiptModal(cur, state.receipt) : ''}
                </div>
            `;

            bindEvents();
        }

        function renderQueueView(cur) {
            const syncedCount = state.queue.filter((q) => q.status === 'Synced').length;
            if (state.queue.length === 0) {
                return `
                    <div class="rjam-off-card rjam-off-empty">
                        <strong>No offline sales in queue</strong>
                        <div class="rjam-off-muted">Sales completed while disconnected will appear here and sync automatically when reconnected.</div>
                    </div>
                `;
            }

            return `
                <div class="rjam-off-card">
                    <div class="rjam-off-row-between" style="margin-bottom:14px;">
                        <div>
                            <h2 class="rjam-off-h2">Offline Sales Queue</h2>
                            <div class="rjam-off-muted">Pending sales sync automatically in order as soon as the server is reachable.</div>
                        </div>
                        ${syncedCount > 0 ? `<button type="button" class="rjam-off-btn is-outline" data-action="clear-synced">Clear Synced (${syncedCount})</button>` : ''}
                    </div>
                    <div class="rjam-off-queue-list">
                        ${state.queue.slice().reverse().map((q) => {
                            const statusCls = safeStatusClass(q.status);
                            return `
                            <div class="rjam-off-queue-item status-${statusCls}">
                                <div class="rjam-off-row-between">
                                    <div>
                                        <strong class="tabular-nums">${esc(q.syncedReceiptNumber || q.offlineReceiptNo)}</strong>
                                        <span class="rjam-off-badge status-${statusCls}">${esc(q.status)}</span>
                                    </div>
                                    <strong class="tabular-nums">${esc(formatMoney(cur, q.totalAmount))}</strong>
                                </div>
                                <div class="rjam-off-muted" style="font-size:0.8rem; margin-top:4px;">
                                    ${esc(new Date(q.createdAt).toLocaleString())} · ${esc(q.paymentMethodLabel)} · ${esc(q.items.map((i) => `${i.name} × ${formatQty(i.quantity)}`).join(', '))}
                                </div>
                                ${q.errorMessage ? `<div class="rjam-off-err">${esc(q.errorMessage)}</div>` : ''}
                                <div class="rjam-off-queue-actions">
                                    <button type="button" class="rjam-off-btn is-outline" data-view-receipt="${esc(q.clientIdempotencyKey)}">Receipt</button>
                                    ${q.status === 'Conflict' ? `<button type="button" class="rjam-off-btn is-primary" data-retry="${esc(q.clientIdempotencyKey)}">Retry Sync</button>` : ''}
                                    ${q.status !== 'Pending' ? `<button type="button" class="rjam-off-link-danger" data-delete-q="${esc(q.clientIdempotencyKey)}">Remove</button>` : ''}
                                </div>
                            </div>
                        `;
                        }).join('')}
                    </div>
                </div>
            `;
        }

        function renderReceiptModal(cur, r) {
            return `
                <div class="rjam-off-modal-backdrop">
                    <div class="rjam-off-modal">
                        <div class="rjam-receipt-printable" style="background:#FFFFFF; border:1px solid #E6DBD1; border-radius:10px; padding:18px;">
                            <div style="text-align:center; border-bottom:1px dashed #D8C9BC; padding-bottom:12px; margin-bottom:12px;">
                                <img src="/images/rjam-logo.png" alt="${esc(state.catalog.bakeryName)}" style="height:42px; width:auto; margin:0 auto 6px auto; display:block;" />
                                <div style="font-weight:700; letter-spacing:0.04em;">${esc((state.catalog.bakeryName || 'RJAM Bakery').toUpperCase())}</div>
                                <div class="rjam-off-muted" style="font-size:0.78rem;">
                                    ${r.syncedReceiptNumber ? 'Official POS Receipt (Synced)' : 'Offline POS Receipt (Pending Server Sync)'}
                                </div>
                                <div class="tabular-nums" style="font-weight:700; margin-top:6px;">${esc(r.syncedReceiptNumber || r.offlineReceiptNo)}</div>
                                <div class="rjam-off-muted" style="font-size:0.76rem;">${esc(new Date(r.createdAt).toLocaleString())}</div>
                            </div>
                            <div style="border-bottom:1px dashed #D8C9BC; padding-bottom:10px; margin-bottom:10px;">
                                ${r.items.map((item) => `
                                    <div class="rjam-off-row-between" style="font-size:0.86rem; margin-bottom:6px;">
                                        <div>
                                            <div style="font-weight:600;">${esc(item.name)}</div>
                                            <div class="rjam-off-muted tabular-nums" style="font-size:0.75rem;">${esc(formatQty(item.quantity))} × ${esc(formatMoney(cur, item.unitPrice))}</div>
                                        </div>
                                        <div class="tabular-nums" style="font-weight:600;">${esc(formatMoney(cur, item.unitPrice * item.quantity))}</div>
                                    </div>
                                `).join('')}
                            </div>
                            <div style="font-size:0.86rem;">
                                <div class="rjam-off-row-between"><span>Subtotal</span><span class="tabular-nums">${esc(formatMoney(cur, r.subTotal))}</span></div>
                                ${r.discountAmount > 0 ? `<div class="rjam-off-row-between" style="color:#3E7B5A;"><span>Discount</span><span class="tabular-nums">-${esc(formatMoney(cur, r.discountAmount))}</span></div>` : ''}
                                <div class="rjam-off-row-between" style="font-weight:700; font-size:1rem; margin:8px 0;"><span>TOTAL</span><span class="tabular-nums">${esc(formatMoney(cur, r.totalAmount))}</span></div>
                                <div class="rjam-off-row-between rjam-off-muted"><span>Paid (${esc(r.paymentMethodLabel)})</span><span class="tabular-nums">${esc(formatMoney(cur, r.amountTendered))}</span></div>
                                <div class="rjam-off-row-between rjam-off-muted"><span>Change</span><span class="tabular-nums">${esc(formatMoney(cur, r.changeAmount))}</span></div>
                            </div>
                        </div>
                        <div class="rjam-off-row-between" style="margin-top:14px;">
                            <button type="button" class="rjam-off-btn is-outline" data-action="print-receipt">Print Receipt</button>
                            <button type="button" class="rjam-off-btn is-primary" data-action="close-receipt">Done</button>
                        </div>
                    </div>
                </div>
            `;
        }

        function bindEvents() {
            const searchEl = container.querySelector('[data-field="search"]');
            if (searchEl) {
                searchEl.addEventListener('input', (e) => {
                    state.search = e.target.value;
                    const pos = e.target.selectionStart;
                    render();
                    const next = container.querySelector('[data-field="search"]');
                    if (next) {
                        next.focus();
                        if (pos !== null) next.setSelectionRange(pos, pos);
                    }
                });
            }

            container.querySelectorAll('[data-cat]').forEach((btn) => {
                btn.addEventListener('click', () => {
                    const val = btn.getAttribute('data-cat');
                    state.categoryId = val === 'all' ? null : Number(val);
                    render();
                });
            });

            container.querySelectorAll('[data-add]').forEach((btn) => {
                btn.addEventListener('click', () => {
                    const pid = Number(btn.getAttribute('data-add'));
                    const item = (state.catalog.items || []).find((x) => x.productId === pid);
                    if (!item) return;
                    const reserved = computePendingReservedByProduct(state.queue).get(pid) || 0;
                    const maxStock = Math.max(0, roundMoney((Number(item.availableStoreStock) || 0) - reserved));
                    const existing = state.cart.find((c) => c.productId === pid);
                    if (!existing) {
                        if (maxStock <= 0) return;
                        state.cart.push({
                            productId: pid,
                            name: item.name,
                            unitPrice: roundMoney(item.sellingPrice),
                            unitCode: item.unitCode || 'pcs',
                            maxStock,
                            quantity: Math.min(1, maxStock)
                        });
                    } else if (existing.quantity + 1 <= maxStock) {
                        existing.quantity += 1;
                    }
                    const tot = cartTotal();
                    if (state.tendered < tot) state.tendered = tot;
                    render();
                });
            });

            container.querySelectorAll('[data-inc]').forEach((btn) => {
                btn.addEventListener('click', () => {
                    const line = state.cart.find((c) => c.productId === Number(btn.getAttribute('data-inc')));
                    if (line && line.quantity + 1 <= line.maxStock) {
                        line.quantity += 1;
                        if (state.tendered < cartTotal()) state.tendered = cartTotal();
                        render();
                    }
                });
            });

            container.querySelectorAll('[data-dec]').forEach((btn) => {
                btn.addEventListener('click', () => {
                    const pid = Number(btn.getAttribute('data-dec'));
                    const line = state.cart.find((c) => c.productId === pid);
                    if (!line) return;
                    line.quantity -= 1;
                    if (line.quantity <= 0) state.cart = state.cart.filter((c) => c.productId !== pid);
                    render();
                });
            });

            container.querySelectorAll('[data-remove]').forEach((btn) => {
                btn.addEventListener('click', () => {
                    const pid = Number(btn.getAttribute('data-remove'));
                    state.cart = state.cart.filter((c) => c.productId !== pid);
                    render();
                });
            });

            const discountEl = container.querySelector('[data-field="discount"]');
            if (discountEl) {
                discountEl.addEventListener('change', (e) => {
                    state.discount = Math.max(0, roundMoney(e.target.value));
                    render();
                });
            }

            const pmEl = container.querySelector('[data-field="paymentMethod"]');
            if (pmEl) {
                pmEl.addEventListener('change', (e) => {
                    state.paymentMethod = Number(e.target.value) || 1;
                });
            }

            const tenderEl = container.querySelector('[data-field="tendered"]');
            if (tenderEl) {
                tenderEl.addEventListener('change', (e) => {
                    state.tendered = Math.max(0, roundMoney(e.target.value));
                    render();
                });
            }

            container.querySelectorAll('[data-tender]').forEach((btn) => {
                btn.addEventListener('click', () => {
                    state.tendered = roundMoney(btn.getAttribute('data-tender'));
                    render();
                });
            });

            container.querySelector('[data-action="clear-cart"]')?.addEventListener('click', () => {
                state.cart = [];
                state.discount = 0;
                state.tendered = 0;
                render();
            });

            container.querySelector('[data-action="tab-pos"]')?.addEventListener('click', () => {
                state.activeTab = 'pos';
                render();
            });

            container.querySelector('[data-action="tab-queue"]')?.addEventListener('click', () => {
                state.activeTab = 'queue';
                render();
            });

            container.querySelector('[data-action="sync-now"]')?.addEventListener('click', async () => {
                render();
                const res = await syncNow();
                state.bannerMessage = res.lastMessage || (res.pendingCount === 0 ? 'All sales are up to date.' : '');
                await loadLocal();
                render();
            });

            container.querySelector('[data-action="close-overlay"]')?.addEventListener('click', () => {
                if (onClose) onClose();
            });

            container.querySelector('[data-action="checkout"]')?.addEventListener('click', async () => {
                if (state.cart.length === 0 || state.tendered < cartTotal()) return;
                const saved = await enqueueSale({
                    paymentMethod: state.paymentMethod,
                    discountAmount: state.discount,
                    amountTendered: state.tendered,
                    items: state.cart
                });

                state.cart = [];
                state.discount = 0;
                state.tendered = 0;
                state.receipt = saved;

                if (navigator.onLine !== false) {
                    const syncRes = await syncNow();
                    const refreshedQueue = await dbGetAllQueue();
                    const updated = refreshedQueue.find((x) => x.clientIdempotencyKey === saved.clientIdempotencyKey);
                    if (updated) state.receipt = updated;
                    state.bannerMessage = syncRes.lastMessage || 'Sale saved locally.';
                } else {
                    state.bannerMessage = `Sale ${saved.offlineReceiptNo} queued offline. It will sync automatically when Wi-Fi returns.`;
                }

                await loadLocal();
                render();
            });

            container.querySelector('[data-action="print-receipt"]')?.addEventListener('click', () => window.print());
            container.querySelector('[data-action="close-receipt"]')?.addEventListener('click', () => {
                state.receipt = null;
                render();
            });

            container.querySelector('[data-action="clear-synced"]')?.addEventListener('click', async () => {
                await clearSynced();
                await loadLocal();
                render();
            });

            container.querySelectorAll('[data-retry]').forEach((btn) => {
                btn.addEventListener('click', async () => {
                    const res = await retryConflict(btn.getAttribute('data-retry'));
                    state.bannerMessage = res?.lastMessage || '';
                    await loadLocal();
                    render();
                });
            });

            container.querySelectorAll('[data-delete-q]').forEach((btn) => {
                btn.addEventListener('click', async () => {
                    await removeQueueItem(btn.getAttribute('data-delete-q'));
                    await loadLocal();
                    render();
                });
            });

            container.querySelectorAll('[data-view-receipt]').forEach((btn) => {
                btn.addEventListener('click', () => {
                    const key = btn.getAttribute('data-view-receipt');
                    const found = state.queue.find((q) => q.clientIdempotencyKey === key);
                    if (found) {
                        state.receipt = found;
                        render();
                    }
                });
            });
        }

        return {
            async init() {
                await loadLocal();
                render();
                if (standalone && navigator.onLine !== false) {
                    await refreshCatalogFromServer();
                    await syncNow();
                    await loadLocal();
                    render();
                }
            },
            async refresh(msg) {
                if (msg) state.bannerMessage = msg;
                await loadLocal();
                render();
            }
        };
    }

    async function openOfflineOverlay(initialTab = 'pos') {
        if (!overlayHost) {
            overlayHost = document.createElement('div');
            overlayHost.id = 'rjam-offline-pos-overlay';
            overlayHost.className = 'rjam-offpos-overlay';
            document.body.appendChild(overlayHost);
        }
        overlayHost.style.display = 'block';

        activeController = createTerminalController(overlayHost, {
            standalone: false,
            initialTab,
            onClose: () => {
                closeOfflineOverlay();
                // If Blazor circuit died while offline and network is back, reload cleanly into live POS
                const modal = document.getElementById('components-reconnect-modal');
                if (modal && modal.classList.contains('components-reconnect-show') && navigator.onLine !== false) {
                    window.location.reload();
                }
            }
        });
        await activeController.init();
    }

    function closeOfflineOverlay() {
        if (overlayHost) {
            overlayHost.style.display = 'none';
            overlayHost.innerHTML = '';
        }
        activeController = null;
    }

    function watchPosConnectivity() {
        if (!syncTimer) {
            syncTimer = setInterval(async () => {
                if (navigator.onLine === false) return;
                const queue = await dbGetAllQueue();
                if (queue.some((q) => q.status === 'Pending')) {
                    await syncNow();
                }
            }, 15000);
        }

        if (!reconnectObserver && typeof MutationObserver !== 'undefined') {
            reconnectObserver = new MutationObserver(() => {
                if (!window.location.pathname.toLowerCase().startsWith('/pos')) return;
                const modal = document.getElementById('components-reconnect-modal');
                if (!modal) return;
                const disconnected = modal.classList.contains('components-reconnect-show')
                    || modal.classList.contains('components-reconnect-failed')
                    || modal.classList.contains('components-reconnect-rejected');
                if (disconnected && (!overlayHost || overlayHost.style.display === 'none')) {
                    openOfflineOverlay('pos');
                }
            });
            reconnectObserver.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] });
        }
    }

    window.addEventListener('offline', () => {
        notifyListeners(0, 'Connection lost. Offline POS mode is ready.');
        if (window.location.pathname.toLowerCase().startsWith('/pos') && (!overlayHost || overlayHost.style.display === 'none')) {
            openOfflineOverlay('pos');
        }
    });

    window.addEventListener('online', async () => {
        await notifyListeners(0, 'Connection restored. Checking pending offline sales...');
        await syncNow();
    });

    async function clearCatalogCacheIfQueueEmpty() {
        try {
            const queue = await dbGetAllQueue();
            if (queue.some((q) => q.status === 'Pending' || q.status === 'Conflict')) {
                return false;
            }
            const db = await openDb();
            await new Promise((resolve, reject) => {
                const tx = db.transaction(META_STORE, 'readwrite');
                tx.objectStore(META_STORE).delete('catalog');
                tx.oncomplete = () => resolve();
                tx.onerror = () => reject(tx.error);
            });
            try { localStorage.removeItem('rjam_meta_catalog'); } catch { }
            return true;
        } catch {
            try { localStorage.removeItem('rjam_meta_catalog'); } catch { }
            return false;
        }
    }

    document.addEventListener('submit', (ev) => {
        const form = ev.target;
        if (form instanceof HTMLFormElement && form.classList.contains('rjam-logout-form')) {
            clearCatalogCacheIfQueueEmpty();
        }
    });

    // Register Service Worker only on secure contexts (localhost / HTTPS) to avoid console errors on HTTP LAN IPs
    if ('serviceWorker' in navigator && window.isSecureContext) {
        window.addEventListener('load', () => {
            navigator.serviceWorker.register('/sw.js').catch(() => { });
        });
    }

    window.RjamOfflinePos = {
        async attachPosPage(ref) {
            dotNetRef = ref;
            watchPosConnectivity();
            const state = await getSummaryState();
            if (state.pendingCount > 0 && navigator.onLine !== false) {
                setTimeout(() => { syncNow(); }, 250);
            }
            return state;
        },
        detachPosPage() {
            dotNetRef = null;
        },
        saveCatalogSnapshot,
        getSummaryState,
        syncNow,
        enqueueSale,
        openOfflineOverlay,
        closeOfflineOverlay,
        clearCatalogCacheIfQueueEmpty,
        async mountStandalone(el) {
            if (!el) return;
            watchPosConnectivity();
            activeController = createTerminalController(el, { standalone: true, initialTab: 'pos' });
            await activeController.init();
        }
    };

    // Auto-mount standalone offline shell without requiring inline <script>
    function autoMountStandalone() {
        const root = document.getElementById('offline-pos-root');
        if (root) {
            window.RjamOfflinePos.mountStandalone(root);
        }
    }

    if (document.readyState === 'loading') {
        window.addEventListener('DOMContentLoaded', autoMountStandalone);
    } else {
        autoMountStandalone();
    }
})();
