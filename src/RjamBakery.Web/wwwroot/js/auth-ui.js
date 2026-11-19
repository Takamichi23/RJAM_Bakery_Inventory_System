(() => {
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
    }

    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initAuthUi);
    } else {
        initAuthUi();
    }

    document.addEventListener('submit', (ev) => {
        const form = ev.target;
        if (!(form instanceof HTMLFormElement) || !form.classList.contains('auth-submit-form')) return;
        const overlay = document.getElementById('rjam-auth-submit-overlay');
        const label = document.getElementById('rjam-auth-submit-text');
        const customText = form.getAttribute('data-loading-text');
        if (label && customText) {
            label.textContent = customText;
        }
        overlay?.classList.add('is-active');
    });
})();
