// ========================================
// JobFlow AI — Site JavaScript
// ========================================

document.addEventListener('DOMContentLoaded', function () {

    // --- Sidebar Toggle (Mobile) ---
    const sidebarToggle = document.getElementById('sidebarToggle');
    const sidebar = document.getElementById('sidebar');

    if (sidebarToggle && sidebar) {
        sidebarToggle.addEventListener('click', function () {
            sidebar.classList.toggle('show');
        });

        // Close sidebar when clicking outside on mobile
        document.addEventListener('click', function (e) {
            if (window.innerWidth < 992 &&
                sidebar.classList.contains('show') &&
                !sidebar.contains(e.target) &&
                !sidebarToggle.contains(e.target)) {
                sidebar.classList.remove('show');
            }
        });
    }

    // --- Clock ---
    const timeEl = document.getElementById('currentTime');
    if (timeEl) {
        function updateClock() {
            const now = new Date();
            timeEl.textContent = now.toLocaleTimeString('en-US', {
                hour: '2-digit',
                minute: '2-digit',
                hour12: true
            }) + ' · ' + now.toLocaleDateString('en-US', {
                month: 'short',
                day: 'numeric',
                year: 'numeric'
            });
        }
        updateClock();
        setInterval(updateClock, 60000);
    }

    // --- Search Debounce ---
    const searchInput = document.getElementById('searchInput');
    if (searchInput) {
        let debounceTimer;
        searchInput.addEventListener('keypress', function (e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                document.getElementById('filterForm').submit();
            }
        });
    }

    // --- Auto-dismiss alerts ---
    const alerts = document.querySelectorAll('.alert-dismissible');
    alerts.forEach(function (alert) {
        setTimeout(function () {
            const bsAlert = bootstrap.Alert.getOrCreateInstance(alert);
            if (bsAlert) {
                alert.classList.remove('show');
                setTimeout(() => alert.remove(), 300);
            }
        }, 5000);
    });

    // --- Animate table rows on scroll ---
    const rows = document.querySelectorAll('.job-row');
    rows.forEach(function (row, i) {
        row.style.animationDelay = (i * 0.03) + 's';
    });

    // --- Form submission loading states ---
    const forms = document.querySelectorAll('form[method="post"]');
    forms.forEach(function (form) {
        form.addEventListener('submit', function () {
            const submitBtn = form.querySelector('button[type="submit"]');
            if (submitBtn && !submitBtn.disabled && !submitBtn.dataset.noLoading) {
                const originalText = submitBtn.innerHTML;
                submitBtn.dataset.originalText = originalText;
                submitBtn.disabled = true;
                submitBtn.innerHTML = '<span class="spinner-border spinner-border-sm me-2"></span> Processing...';

                // Re-enable after 30 seconds as failsafe
                setTimeout(function () {
                    submitBtn.disabled = false;
                    submitBtn.innerHTML = originalText;
                }, 30000);
            }
        });
    });

    // --- Tooltip init ---
    const tooltipTriggerList = document.querySelectorAll('[title]');
    tooltipTriggerList.forEach(function (el) {
        new bootstrap.Tooltip(el, { trigger: 'hover' });
    });

    // --- Automatic Redirection to Live Activity on Telegram Message ---
    const currentPath = window.location.pathname.toLowerCase();
    if (currentPath !== "/telegram/progress" && currentPath !== "/telegram/getlivestatus") {
        setInterval(async () => {
            try {
                const response = await fetch('/Telegram/IsExecuting');
                if (response.ok) {
                    const data = await response.json();
                    if (data && data.active) {
                        window.location.href = "/Telegram/Progress";
                    }
                }
            } catch (e) {}
        }, 1500);
    }
});
