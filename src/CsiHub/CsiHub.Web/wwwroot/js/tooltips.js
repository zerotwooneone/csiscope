window.initTooltips = function () {
    const nodes = document.querySelectorAll('[data-bs-toggle="tooltip"]');
    nodes.forEach(function (node) {
        const existing = bootstrap.Tooltip.getInstance(node);
        if (existing) {
            existing.dispose();
        }
        new bootstrap.Tooltip(node);
    });
};
