(() => {
    const control = document.querySelector('.rating-control');
    const updateRating = () => {
        const selected = control.querySelector('input[name="Rating"]:checked');
        const value = selected?.value === '' ? null : Number(selected?.value ?? 0);
        control.querySelectorAll('[data-star]').forEach(star => {
            star.querySelector('.star-fill').style.width = `${Math.max(0, Math.min(2, (value ?? 0) - (Number(star.dataset.star) - 1) * 2)) * 50}%`;
        });
        document.getElementById('rating-value').textContent = value === null
            ? 'Não avaliado' : `${(value / 2).toLocaleString('pt-BR')} de 5 estrelas`;
    };
    control.addEventListener('change', updateRating);
    updateRating();
    const status = document.getElementById('library-status');
    const reason = document.getElementById('abandonment-field');
    const updateReason = () => {
        reason.hidden = status.value !== '3';
        reason.querySelector('textarea').disabled = status.value !== '3';
    };
    status.addEventListener('change', updateReason);
    updateReason();
})();
