(() => {
    const confirmations = document.getElementById('platform-removal-confirmations');
    if (!confirmations) return;
    const update = () => {
        let anyRemoved = false;
        confirmations.querySelectorAll('[data-removal-group]').forEach(row => {
            const selected = document.getElementById(`platform-${row.dataset.removalGroup}`).checked;
            const confirm = row.querySelector('input');
            row.hidden = selected;
            confirm.disabled = selected;
            confirm.required = !selected;
            if (selected) confirm.checked = false;
            anyRemoved ||= !selected;
        });
        confirmations.hidden = !anyRemoved;
    };
    document.querySelectorAll('input[name="SelectedPlatforms"]').forEach(input => input.addEventListener('change', update));
    update();
})();
