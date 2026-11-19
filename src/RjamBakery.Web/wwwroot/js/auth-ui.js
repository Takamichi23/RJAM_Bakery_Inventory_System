(() => {
    function buildPostLoginSkeletonHtml() {
        const cards = Array.from({ length: 8 }, () => `
            <div style="background:#FFFFFF; border:1px solid #E6DBD1; border-radius:12px; padding:16px;">
                <div class="rjam-skel-box" style="width:58%; height:14px;"></div>
                <div class="rjam-skel-box" style="width:42%; height:30px; margin-top:10px;"></div>
            </div>
        `).join('');

        const rows = Array.from({ length: 5 }, () => `
            <div class="rjam-skel-box" style="width:100%; height:36px; margin-bottom:10px;"></div>
        `).join('');

        return `
            <div style="background:#FFFFFF; border:1px solid #E6DBD1; border-radius:14px; padding:18px; margin-bottom:18px;">
                <div class="rjam-skel-box" style="width:260px; height:24px;"></div>
                <div class="rjam-skel-box" style="width:380px; max-width:90%; height:14px; margin-top:8px;"></div>
            </div>
            <div style="display:grid; grid-template-columns:repeat(auto-fill, minmax(220px, 1fr)); gap:14px; margin-bottom:18px;">
                ${cards}
            </div>
            <div style="background:#FFFFFF; border:1px solid #E6DBD1; border-radius:14px; padding:18px;">
                <div class="rjam-skel-box" style="width:220px; height:22px; margin-bottom:14px;"></div>
                ${rows}
            </div>
        `;
    }

    function initAuthUi() {
        const splash = document.getElementById('rjam-prelogin-splash');
        if (splash) {
            let shown = false;
            try { shown = sessionStorage.getItem('rjam_prelogin_shown') === '1'; } catch { }
            if (shown) {
                splash.classList.add('is-dismissed');
            } else {
                try { sessionStorage.setItem('rjam_prelogin_shown', '1'); } catch { }
            }
        }

        const postLoginHost = document.getElementById('rjam-postlogin-skeleton');
        if (postLoginHost) {
            let pendingSkeleton = false;
            try { pendingSkeleton = sessionStorage.getItem('rjam_show_postlogin_skeleton') === '1'; } catch { }
            if (pendingSkeleton) {
                try { sessionStorage.removeItem('rjam_show_postlogin_skeleton'); } catch { }
                postLoginHost.innerHTML = buildPostLoginSkeletonHtml();
                postLoginHost.classList.add('is-active');
                setTimeout(() => {
                    postLoginHost.classList.remove('is-active');
                    postLoginHost.innerHTML = '';
                }, 850);
            }
        }
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initAuthUi);
    } else {
        initAuthUi();
    }

    document.addEventListener('click', (ev) => {
        const target = ev.target instanceof Element ? ev.target.closest('[data-toggle-password]') : null;
        if (!(target instanceof HTMLButtonElement)) return;

        const inputId = target.getAttribute('data-toggle-password');
        const input = (inputId ? document.getElementById(inputId) : null)
            || target.closest('.auth-password-wrap')?.querySelector('input');
        if (!(input instanceof HTMLInputElement)) return;

        const revealing = input.type === 'password';
        input.type = revealing ? 'text' : 'password';
        target.classList.toggle('is-visible', revealing);
        target.setAttribute('aria-pressed', revealing ? 'true' : 'false');
        target.setAttribute('aria-label', revealing ? 'Hide password' : 'Show password');
        target.setAttribute('title', revealing ? 'Hide password' : 'Show password');
    });

    document.addEventListener('submit', (ev) => {
        const form = ev.target;
        if (!(form instanceof HTMLFormElement)) return;

        if (form.classList.contains('rjam-logout-form')) {
            try {
                sessionStorage.removeItem('rjam_prelogin_shown');
                sessionStorage.removeItem('rjam_show_postlogin_skeleton');
            } catch { }
            return;
        }

        if (!form.classList.contains('auth-submit-form')) return;
        try { sessionStorage.setItem('rjam_show_postlogin_skeleton', '1'); } catch { }

        const overlay = document.getElementById('rjam-auth-submit-overlay');
        const label = document.getElementById('rjam-auth-submit-text');
        const customText = form.getAttribute('data-loading-text');
        if (label && customText) {
            label.textContent = customText;
        }
        overlay?.classList.add('is-active');
    });
})();
