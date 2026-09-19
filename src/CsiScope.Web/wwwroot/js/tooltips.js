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

// Geometry drag-and-drop support. Two global listeners keep the Blazor side
// free of per-pixel dragover traffic and satisfy Firefox, which requires
// dataTransfer.setData() in dragstart for a drag to begin at all.
document.addEventListener('dragstart', function (e) {
    const el = e.target instanceof Element ? e.target.closest('[data-geom-drag]') : null;
    if (el && e.dataTransfer) {
        e.dataTransfer.setData('text/plain', el.getAttribute('data-geom-drag'));
        e.dataTransfer.effectAllowed = 'move';
    }
});

document.addEventListener('dragover', function (e) {
    const el = e.target instanceof Element ? e.target.closest('[data-geom-drop]') : null;
    if (el) {
        e.preventDefault();
        if (e.dataTransfer) {
            e.dataTransfer.dropEffect = 'move';
        }
    }
});
