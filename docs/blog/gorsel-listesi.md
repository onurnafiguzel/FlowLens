# Görsel çekim listesi — `article-01.md`

## Durum

Dört diyagram artık **yazının içinde**, canlı Mermaid bloğu olarak. Ekran görüntüsü almana
gerek yok; `article-01.md`'yi GitHub'da açtığında render olurlar.

| Yazıdaki yeri | Diyagram | Kaynağı |
|---|---|---|
| §2, "Çözümün şekli" | Mimari şema | elle yazıldı, `tools/mermaid-check` ile doğrulandı |
| §5, "Tek soru, tek sayfa" | Sepet silme akışı | `out/flows/delete-api-cart-items-productid-guid.md` |
| §5, "İki karşıt sayfa" | Discovery arama akışı | `out/flows/post-api-discovery-search.md` |
| §5, "Modül bağımlılık grafiği" | 8 modül, 9 kenar | `out/modules/dependencies.md` |

Bağımlılık grafiğinin **legend tablosu** da yazıya tablo olarak kondu, o da ekran görüntüsü
istemiyor.

Beşinin tamamı `tools/mermaid-check` ile parse edildi: **5/5, exit 0.** Yani GitHub'ın kullandığı
kütüphane bu blokları render eder.

**Medium'a taşırken:** Medium Mermaid render etmiyor. `article-01.md`'yi GitHub'da aç, render
olmuş diyagramların ekran görüntüsünü oradan al, Medium'a görsel olarak yapıştır. Diyagramların
kaynağı yazının içinde durduğu için ayrı dosya aramana gerek kalmıyor.

---

## Kalan 12 ekran görüntüsü

Yazıdaki `[GÖRSEL N]` işaretleri okuma sırasına göre 1'den 12'ye numaralı, aşağıdakilerle birebir
eşleşiyor.

### 1 — Giriş sayfası, tablolar
**Dosya:** `out/README.md` · **Nerede:** GitHub

`## Modüller` tablosunun tamamı ve `## Akışlar` tablosunun ilk 8 satırı. Tek karede olmuyorsa iki
kare al.

**Görünmeli:** modül adlarının bağlantı olduğu, sayıların sağa yaslı sütunlarda durduğu.

### 2 — Giriş sayfası, kapsam uyarısı ⭐
**Dosya:** `out/README.md` · **Nerede:** GitHub

Sayfanın başındaki **"Kapsam uyarısı"** blockquote'u **ve** sonundaki **"Veri katmanına
dokunmayan (2)"** bölümü. İkisi dosyanın iki ucunda; iki ayrı kare de olur.

**Neden ikisi de:** biri aracın ne göremediğini, diğeri boş sonucun da bir sonuç olduğunu
söylüyor. Yazının tezi bu iki blokta duruyor.

### 3 — Sepet silme, veri katmanı ve adım listesi
**Dosya:** `out/flows/delete-api-cart-items-productid-guid.md` · **Nerede:** GitHub

`## Çağrı sırası` ve `## Veri katmanı` bölümleri.

**Görünmeli:** 2. ve 3. adımdaki ***(koşullu)*** işareti, ve veri katmanı satırındaki `WR` ile üç
kolon adı. Yazının o paragrafı tam olarak bunları anlatıyor.

### 4 — Sepet silme, bilinen sınırlar
**Dosya:** aynı · **Nerede:** GitHub

`## Bilinen sınırlar` bölümü.

**Görünmeli:** `unmapped-column`, `second-class-evidence`, `ambiguous-implementation` kod adları.
Blok uzun; kod adları ve ilk satırları görünecek şekilde kırpabilirsin.

### 5 — Checkout, sadeleştirme oranı
**Dosya:** `out/flows/post-api-ordering-checkout.md` · **Nerede:** GitHub

`## Diyagram neyi göstermiyor` başlığı ve altındaki tek satır: *"Gösterilen 24 node; ham yürüyüş
192 node'a ulaşıyor."* Tüm sayfayı çekmene gerek yok.

### 6 — Karşıt vaka, kolonu olmayan tablo
**Dosya:** `out/flows/get-api-catalog-products.md` · **Nerede:** GitHub

`## Veri katmanı` tablosu, tek satır.

**Görünmeli:** `Erişim` sütununda `R`, `Kolonlar` sütununda tire. Yazı o tireyi anlatıyor.

### 7 — İhlal adayları
**Dosya:** `out/modules/dependencies.md` · **Nerede:** GitHub

`## İhlal adayları` bölümü.

**Görünmeli:** *"Bu bir hüküm değil, bir işaret"* cümlesi, iki `Shared →` başlığı ve `IDataSeeder`
kanıt satırları.

### 8 — Tüketicisiz event
**Dosya:** `out/modules/Ordering.md` · **Nerede:** GitHub

`## Event'ler` tablosu.

**Görünmeli:** `OrderCancelled` satırında `Tüketiciler` sütunu kalın **yok** diyor. Karşılaştırma
için `OrderPaid` satırı da karede olsun.

### 9 — Geriye doğru sorgu
**Nerede:** Terminal ya da Postman

```bash
dotnet run --project src/FlowLens.Api -c Release
# ayri bir terminalde:
curl "http://localhost:5000/backward?node=column%3Acart.carts.Items"
```

**Görünmeli:** `entryPoints` bölümü, beş giriş noktası ve içlerinde
`POST /api/ordering/checkout`. Yazının "beşincisi başka bir modül" cümlesi buna dayanıyor.

### 10 — Triage raporu ⭐
**Nerede:** Terminal

```bash
dotnet run --project src/FlowLens.Cli -c Release -- triage --stack-trace tests/FlowLens.Tests/Fixtures/StackTraces/A-inventory-reserve.txt
```

**Çek:** `## Giriş noktaları`, `## Bu noktadan sonra dokunulan tablolar`, `## Son değişiklikler`.
Üçü ardışık, tek uzun karede sığar.

**Görünmeli:** iki endpoint (Inventory ve Ordering), iki tablo erişimleriyle, commit listesi.

### 11 — Recall tablosu
**Dosya:** `evals/report.md` · **Nerede:** GitHub ya da VS Code

Eksen eksen recall/precision tablosu.

**Not:** dosyada ondalık ayırıcı nokta (`%97.1`), yazıda virgül (`%97,1`). Aynı değer, Türkçe
yazım; görsel ile metin arasındaki bu fark normal.

### 12 — Körlüğün ilanı ⭐
**Nerede:** Terminal, **10 ile aynı komut**, farklı kırpma

`### ⚠ Hata noktası, graph'ın bakamadığı bir bölgede` bloğu.

**Görünmeli:** uyarı satırı ve altındaki `raw SQL reaches the database outside the model...`
kanıtı, `NaiveReservationStrategy.cs:37` dahil.

Yazının tezini taşıyan tek kare bu: araç, hatanın olduğu tam satırda kendi körlüğünü ilan ediyor.

---

## Öncelik

Vaktin kısıtlıysa şu dördü yeter: **2, 10, 12** ve **5**. Diyagramlar zaten yazının içinde
olduğu için görsel yükü belirgin şekilde azaldı.

Terminal gerektirenler: **9, 10, 12** (10 ve 12 aynı komut).
