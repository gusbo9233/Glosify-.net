export const escapeHtml = (text) => {
    const div = document.createElement('div');
    div.textContent = text ?? '';
    return div.innerHTML;
};

export const formatChatDate = (value) => {
    if (!value) return '';
    const date = new Date(value);
    if (Number.isNaN(date.getTime())) return '';
    return new Intl.DateTimeFormat(undefined, {
        month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit',
    }).format(date);
};
// Only runtime-created quiz ids become links; names are always plain text.
export const quizLink = (document, part) => {
    if (!/^[0-9a-f]{8}-(?:[0-9a-f]{4}-){3}[0-9a-f]{12}$/i.test(part.text || '')) return null;
    const link = document.createElement('a');
    link.className = 'btn-secondary';
    link.href = `/Quizzes/Details/${encodeURIComponent(part.text)}`;
    link.textContent = part.title;
    link.dir = 'auto';
    return link;
};
