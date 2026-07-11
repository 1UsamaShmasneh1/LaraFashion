window.storeVisits = { record: async function () { try { await fetch('/api/store/visit', { method: 'POST', credentials: 'same-origin' }); } catch { } } };
