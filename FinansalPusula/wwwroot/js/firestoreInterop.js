// firestoreInterop.js
// Firebase gerekmeksizin tamamen localStorage üzerinde çalışan portfolio katmanı.
// API key yokken bile portföy işlemleri kayıt/okuma/silme yapar.

window.firestoreInterop = {
    _key: 'finansalPusula_transactions',

    _load: function () {
        try {
            const raw = localStorage.getItem(this._key);
            return raw ? JSON.parse(raw) : [];
        } catch (e) {
            console.warn('localStorage okuma hatası:', e);
            return [];
        }
    },

    _save: function (items) {
        try {
            localStorage.setItem(this._key, JSON.stringify(items));
        } catch (e) {
            console.warn('localStorage yazma hatası:', e);
        }
    },

    addTransaction: async function (transaction) {
        try {
            const items = this._load();
            // Blazor'dan gelen nesnenin id alanı yoksa oluştur
            if (!transaction.id) {
                transaction.id = crypto.randomUUID
                    ? crypto.randomUUID()
                    : Date.now().toString(36) + Math.random().toString(36).substr(2);
            }
            items.push(transaction);
            this._save(items);
            console.log('İşlem kaydedildi (localStorage):', transaction.id);
        } catch (e) {
            console.error('addTransaction hatası:', e);
        }
    },

    getTransactions: async function () {
        try {
            const items = this._load();
            // Blazor model alanı adlarını normalize et (camelCase → PascalCase gibi durumlara karşı)
            return items.map(t => ({
                id:          t.id          || t.Id          || '',
                tarih:       t.tarih       || t.Tarih       || new Date().toISOString(),
                islemTipi:   t.islemTipi   !== undefined ? t.islemTipi   : (t.IslemTipi   !== undefined ? t.IslemTipi   : 0),
                sembol:      t.sembol      || t.Sembol      || '',
                adet:        t.adet        || t.Adet        || 0,
                birimFiyat:  t.birimFiyat  || t.BirimFiyat  || 0
            }));
        } catch (e) {
            console.error('getTransactions hatası:', e);
            return [];
        }
    },

    deleteTransaction: async function (id) {
        try {
            let items = this._load();
            items = items.filter(t => (t.id || t.Id) !== id);
            this._save(items);
            console.log('İşlem silindi (localStorage):', id);
        } catch (e) {
            console.error('deleteTransaction hatası:', e);
        }
    },

    // Tüm verileri sıfırlar (test/geliştirme için)
    clearAll: function () {
        localStorage.removeItem(this._key);
        console.log('Tüm işlemler silindi.');
    }
};
