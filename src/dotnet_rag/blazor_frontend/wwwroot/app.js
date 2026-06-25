window.ragApp = {
    saveSettings: (json) => localStorage.setItem('rag-settings', json),
    loadSettings: () => localStorage.getItem('rag-settings'),
    clearSettings: () => localStorage.clear()
};
