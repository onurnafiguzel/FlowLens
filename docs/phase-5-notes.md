# Faz 5 — Dokümantasyon & görselleştirme: notlar

LLM yok, solution yüklenmiyor. Girdi yalnız `graph.json`; çıktı 37 markdown dosyası.

```
flowlens docs -o out/                                          hepsi
flowlens docs -o out/ --endpoint "POST /api/ordering/checkout"  tek akış
flowlens docs -o out/ --module Ordering                         tek modül
```

---

## 1. Ne üretiliyor — 37 dosya

| | adet |
|---|---:|
| `flows/<method>-<route>.md` | 25 |
| `modules/<Modül>.md` | 10 |
| `modules/dependencies.md` | 1 |
| `README.md` | 1 |

Filtreli koşuda `README.md` **yazılmaz**: kısmi çıktıdan üretilen bir index olmayan dosyalara
bağlantı verir. Bozuk bir index, index olmamasından kötüdür. `AFilteredRunWritesNoIndex` bunu
teste bağlıyor.

### Neden 10 modül, kabul kriterindeki 9 değil

Graph'ta 10 farklı `module` değeri var: 8 iş modülü + `Host` + `Shared`. Kriterdeki 9 sayısı
`Shared`'ı saymıyordu.

Sayıya uymak için `Shared`'ı atlamak, **iki ihlal adayının ikisini de** görünmez kılardı —
çünkü ikisi de `Shared`'tan çıkıyor (`Shared → Catalog`, `Shared → Inventory`). Bir bulguyu
bir sayıya uydurmak için gizlemek, bu projenin tüm fazlarda reddettiği şey.

---

## 2. Üç non-determinizm kaynağı — ve Faz 4'ünkinden farklı ders

`TheSameGraphAlwaysProducesTheSameBytes` ilk koşuda **6 akış sayfasında** kırıldı. Üç bağımsız
sebep vardı:

| # | Sebep | Düzeltme |
|---|---|---|
| 1 | **Komşuluk listesi girdi sırasına bağlıydı** — BFS kenarları geldikleri sırada geziyordu | `outgoing` sözlüğü `ToId → Kind → Mechanism → Evidence` ile sıralanıyor |
| 2 | **`seen` kümesi tie-break'i bloke ediyordu** — bir kept node ikinci bir yoldan erişilince karşılaştırmaya hiç girmiyordu; "kazanan kenar" keşif sırasıyla belirleniyordu | kept node her rotada aday olarak değerlendiriliyor; `seen` yalnız *düşen* node'ları tek kez genişletiyor |
| 3 | **Tie-break kısmi sıralamaydı** — yalnız `Rank(Kind)`, eşitlikte sonuç girdi sırasına düşüyordu | total order: `Kind` → `Label` (ordinal) → `Ambiguous` |

### Ders: sıralamayı deterministik yapmak yetmez, keşfin kendisi deterministik olmalı

Faz 4'ün dersi **çıktı sıralamasıydı**: küme doğruydu, yazılış sırası değişkendi;
`GraphJson.Canonical` çözdü. 2 satırlık gerçek bir değişiklik 216 satırlık diff üretiyordu.

Faz 5'in dersi bir katman daha derinde. Burada **üretilen kümenin kendisi** farklıydı. `seen`,
bir node'a hangi kenarın bağlanacağını keşif sırasına bırakıyordu; iki koşu iki farklı **kenar
kümesi** üretiyordu. Aynı graph, aynı kurallar, farklı cevap.

> Çıktıyı sıralamak bunu **gizlerdi, düzeltmezdi.** Sıralı ama yanlış bir diyagram hâlâ
> deterministik görünür — ve non-determinizmi arayan tek test (byte karşılaştırması) artık
> yeşil olurdu. Kanonik sıralama bir *son adım*tır; keşif algoritmasındaki sıra bağımlılığını
> maskelemesi mümkündür.

Üçü de Mermaid'in parse ettiği, gözle fark edilmeyen değişikliklerdi. Ne kaynak incelemesi ne
`mermaid.parse()` yakalardı; **byte karşılaştırması** yakaladı.

---

## 3. Diyagram daraltma — ölçüldü, dört kural üst üste

25 endpoint'in **tamamı** ölçüldü:

| Politika | ortalama | maksimum (checkout) |
|---|---:|---:|
| Ham (forward, tüm node'lar) | 30 | **192** |
| + utility filtresi | 25 | 181 |
| + katman filtresi (`Method`/`Entity`/`Column` düşer) | 9 | 29 |
| + arayüz daraltma | 7 | 25 |
| + verisiz yaprak budama | **6,4** | **24** |

Son durum: 25 endpoint'in 25'i ≤24 node. İkinci en büyük 12 (`PUT /api/catalog/products/{id:guid}`),
medyan 5. `EveryEndpointFitsInAReadableDiagram` tavanı 25'te tutuyor.

**Atılan her şey sayılıyor.** Checkout sayfasının altındaki satır ölçülmüş hâliyle:
*"Gösterilen 24 node; ham yürüyüş 192 node'a ulaşıyor. Gizlenen: 152 ara çağrı, 11 utility,
4 arayüz bildirimi, 1 veriye ulaşmayan dal."*

### Geçişli daraltma zorunluydu — ve bu fazın en riskli parçası

Planın ilk hâli "iki ucu da hayatta kalan kenarları koru" diyordu. Ölçüm bunu çürüttü: üç dev
endpoint'i **2 node ve 0 kenar** veriyordu, çünkü yolları `Endpoint → Entity → Table` ve `Entity`
katman filtresinde düşüyor. Havada duran iki kutu, cevap gibi görünen bir hiçtir.

Doğru formülasyon: alt graph **düşen node'ların üzerinden geçilerek** kurulur; düşen düğüm kenar
etiketine yazılır (`==>|"Order"|`).

Bu, sessiz hata için ideal zemin: yanlış birleştirilmiş bir kenar var olmayan bir bağ iddia eder,
Mermaid onu memnuniyetle çizer. O yüzden **iki taraftan** kilitlendi:

- `NoNodeIsLeftFloating` — **hiçbir şey kaybolmasın** (tamlık)
- `EveryContractedEdgeCompressesARealPath` — **hiçbir şey uydurulmasın**: diyagram A→B çiziyorsa
  tam graph'ta A'dan B'ye gerçekten erişilebilir olmalı (sağlamlık)

### Budama tuzağı: Discovery

Kural olduğu gibi uygulansaydı `POST /api/discovery/search` diyagramının **en önemli node'u**
silinecekti: `ProductVectorRepository.SearchAsync` — ham SQL'in yapıldığı, yani *"bu modülün
tablosu neden yok"* sorusunun cevabının bulunduğu yer. Hiçbir tabloya ulaşmadığı için verisiz
yaprak sayılıp budanır ve diyagram graph'ın bildiğinden **azını** iddia ederdi.

İstisna: **diagnostic taşıyan node budanmaz**, kesikli kenarlıkla çizilir
(`classDef unseen`). PNG'de doğrulandı (§6.3).

---

## 3b. Q3 revize — subgraph fikri ölçümle reddedildi

Planın Q3'ü şunu diyordu:

> **`subgraph` = modül sınırı.** Node'lar modüle göre kutulanır. Bir okun kutudan çıkması
> "modül sınırı geçildi" demektir — mimari ihlalin görüneceği yer bu.

**Bu okuma çalışmıyordu.** Checkout'ta 33 kenarın **12'si** (%36), ne kaynağının ne hedefinin ait
olduğu bir kutunun içinden geçiyor: altı kenar `Notification`'ın, iki kenar `Inventory`'nin, iki
kenar `Catalog`'un içinden. Kutuyu kesen ilgisiz bir çizgi tam da "bu modüle dokunuyor" gibi
okunuyor. Sinyalin üçte biri yanlışsa okuyucu sinyali gürültüden ayıramaz.

**Karar: subgraph'lar kaldırıldı, modül etikete taşındı** (`Ordering · ordering.orders`).
Etiket her zaman doğru; kutu sınırı %36 yanıltıcıydı. Bozuk bir sinyali korumaktansa doğrusunu
başka yere koymak daha iyi.

Bu değişiklik **yalnız akış diyagramlarına** uygulandı. Modül bağımlılık grafiğinde zaten subgraph
yoktu, 8 node / 9 kenarla okunabilir ve tek kusuru bir etiket yerleşimi — aynı değişikliği oraya
uygulamak için gerekçe yok. `dependencies.md` bayt bayt aynı kaldı.

> **Kayda değer olan kararın kendisi değil, ölçümün kararı değiştirmesi.** Q3 tasarım aşamasında
> gerekçelendirilmiş, makul ve — plana bakınca — ikna edici bir karardı. Onu çürüten şey daha iyi
> bir argüman değil, PNG'ye bakıp "bu çizgi neden Notification kutusunun içinden geçiyor" diye
> sormak ve sonra o soruyu sayan bir metrik yazmak oldu. Faz 3'ün dersi (110 test yeşilken sessiz
> yanlış cevap) burada tasarım kararlarına da uygulanıyor: **gerekçe, ölçümün yerine geçmez.**

---

## 4. "Modül bağımlılığı" tanımı — ölçüldü

Bağımlı sayılma kuralı hedefin **katmanına** dayanır, modül adına değil. Katman node id'sindeki
`ModularCommerce.<Modül>.<Katman>` segmentinden okunur — tahmin yok.

| Kategori | Kural | Ok | Ölçülen |
|---|---|---|---:|
| **Sözleşme çağrısı** — meşru | hedef katman `Contracts`, kenar `CALLS` | `-->` | **10** |
| **Event** — meşru, en gevşek | `PUBLISHES` / `CONSUMES` | `-.->` | **3** |
| **Doğrudan referans** — ⚠ ihlal adayı | hedef `Application`/`Infrastructure`/`Domain` | `==>` | **2** |
| **Shared** — nötr | hedef modül `Shared` | çizilmez, sayılır | **204** |

219 modüller arası kenarın **204'ü (%93) `Shared`'a** gidiyor. Hepsini çizmek `Shared`'ı her şeye
bağlı bir merkez yapar ve hiçbir şey söylemez: `Result`/`Error` kullanmak tasarım gereği.

**Ama tersi çiziliyor.** İki ihlal adayının ikisi de `Shared → modül`:

```
Shared -> Catalog    [Infrastructure]  CatalogDataSeeder.cs:14
Shared -> Inventory  [Infrastructure]  InventoryDataSeeder.cs:18
```

Bağımlılık ters yönde: paylaşılan altyapı modülün içine çağırıyor. Bu, `phase-4-notes.md` §4'te
"tool'un bulduğu 4. şey" olarak kaydedilen `MigrateAndSeedHostedService` bulgusunun **ölçülmüş ve
kanıtlı** hâli.

**Checkout'ta ihlal yok.** Faz 2'de 4 ölçülen cross-module senkron çağrı bugün 7 (graph büyüdü);
**7/7 kategori 1**, yani `Contracts` üzerinden. `ContractCallsAreNotFlaggedAsViolations` bunu
sabitliyor.

> Diyagram "ihlal" demez, **"Contracts dışından doğrudan referans"** der ve `file:line` verir.
> Kararı okuyan verir; araç kuralı uygular, hüküm vermez.

---

## 5. Doğrulama iki katman — ve ikisinin göremediği bir üçüncü

### 5.1 `mermaid.parse()` — tekrarlanan kapı, 26/26

GitHub Mermaid'i istemci tarafında `mermaid` npm paketiyle render eder; aynı paketin `parse()`'ı
tarayıcısız çalışır. `tools/mermaid-check/` bunu her test koşusunda uygular.

Üç şart:

| Şart | Uygulama |
|---|---|
| Opsiyonel kapı, build şartı değil | `dotnet build` ve `flowlens docs` node'a **hiç dokunmaz**; üretim saf C# |
| Sessizce geçmez | `node_modules` yoksa test **fail** eder; mesajda `cd tools/mermaid-check && npm ci` ve *"NO diagram was verified"*. 0 blok bulunursa `exit 2` |
| Sürüm tam sabit | `"mermaid": "11.16.1"`, `"jsdom": "30.0.1"` — `^` yok, `package-lock.json` commit'li |

`jsdom` teknik zorunluluk: mermaid parse ederken bile DOMPurify'ı `window`'a bağlar; bare Node'da
`DOMPurify.addHook is not a function` verir. jsdom penceresi **dinamik import'tan önce** kuruluyor.

**`package.json` + `package-lock.json` → commit.** Sürümü sabitlemenin doğrulanabilir tek yolu
lock dosyası. **`node_modules/` → ignore** (repo kökündeki `.gitignore` zaten içeriyor, ek satır
gerekmedi).

### 5.2 `@mermaid-js/mermaid-cli` repo bağımlılığı DEĞİL

PNG render'ı puppeteer + tam bir tarayıcı çeker. `tools/mermaid-check/`'e eklemek "opsiyonel kapı,
build şartı değil" kararını fiilen bozardı: bir `npm ci` artık tarayıcı indirirdi.

Ayrım: **tekrarlanan kapı** `mermaid.parse()`; PNG incelemesi **tek seferlik görsel doğrulama**.
Bu faz için `npx --yes @mermaid-js/mermaid-cli@11.12.0` ile scratchpad'de koşturuldu, repoya
hiçbir şey girmedi.

### 5.3 Markdown blok kapısı — ikisinin de göremediği kusur

Üretilmiş dosyaları okurken bulundu: **10 modül sayfasının 10'unda** madde listesi ile onu izleyen
kalın başlık arasında boş satır yoktu.

```
- `Payment` — sözleşme, 2 çağrı<br>  `CancelOrderHandler...`
**Bu modüle dokunanlar:**          ← lazy continuation: başlık, son maddenin İÇİNE giriyor
```

Sebep tek bir eksik `AppendLine`. Etkisi sistematik. Ne derleyici, ne 194 test, ne `mermaid.parse()`
görür — dosya sorunsuz parse edilir ve **yanlış render olur**. Tam olarak bu fazın uyarıldığı
sessiz hata sınıfı, sadece Mermaid'de değil markdown'da.

`NoBlockStartsWithoutABlankLineBeforeIt` eklendi: üretilen her `.md`'de, kod bloğu dışında, blok
başlatan bir satır (`#`, `**`, `- `, `|`, `>`) boş olmayan ve aynı bloğu sürdürmeyen bir satırın
hemen ardından gelemez.

**Mutasyon koşusuyla doğrulandı:** düzeltme geri alındığında test 10 sayfayı da isimle bildirerek
düşüyor; geri konduğunda geçiyor. Kuralın kendisi değil, jeneratöre bağlı olduğu kanıtlanmış oldu.

---

## 6. Görsel inceleme — üç diyagram

Parse etmek okunabilir olmak değildir. Üçü yerelde PNG'ye render edilip **gerçekten incelendi**.

### 6.1 `POST /api/ordering/checkout` — 24 node, 2013×2190 px · **okunabilir**

İlk inceleme subgraph'lı sürümü **okunaksız** buldu: `HandleAsync`'in 17 oku iki yoğun
neredeyse-paralel demet oluşturuyordu ve bu okların bir kısmı ilgisiz modül kutularının içinden
geçiyordu. Ölçüm bunu doğruladı (33 kenarın 12'si yabancı kutu geçişi) ve subgraph'lar kaldırıldı
(§Q3-revize).

Yeni hâlinde gördüklerim:

- **Fan artık takip edilebiliyor.** Oklar `HandleAsync`'ten çıkar çıkmaz ayrışıyor ve her hedef
  kendi satırına iniyor. Tablolara giden kalın kenarların yaklaşma açıları belirgin.
- **Zayıf nokta soldaki demet.** `HandleAsync`'in üstteki hedeflerine giden ~6 ince çizgi,
  x≈400–560 arasında 600 px boyunca yan yana ilerliyor. Tam çözünürlükte aralarındaki boşluk
  görülüyor ve tek tek ayrılabiliyorlar; yavaşladığım tek yer burası. Ölçülen en uzun paralel koşu
  606 px (öncesi 1008 px).
- **Kutuyu kesen ok yok** — yapısal olarak imkânsız, kutu kalmadı.
- İki kesişme gözle görülüyor: üst sağda `CartRecord` bölgesinde
  (`PostgresCartRepository.RemoveAsync → IsDatabaseUnavailable` ile `GetAsync → cart.carts`) ve
  altta `Order` etiketi çevresinde (`AddAsync → orders` ile `GetByIdempotencyKeyAsync → outbox`).
  İkisinde de etiketler kendi kenarlarının üzerinde ve okunaklı.
- Kesikli `OrderPaid` okları, `(ambiguous)` etiketleri ve `unseen` kesikli kutuları ilk bakışta
  seçiliyor.
- Modül artık her kutunun ilk satırında (`Ordering ·`, `Cart ·`) — kutular **genişlemedi**,
  ikinci satıra sarıyor.

Kalan gerçek sınır boyut: 2013×2190 px, GitHub'ın ~896 px'lik sütununa 0,45× ölçekle sığıyor.
Satır içi okunmuyor; tam boyutta açmak gerekiyor — bu, yerleşimin değil ölçeğin sınırı ve
§9.4'te açık madde olarak duruyor.

### 6.2 Modül bağımlılık grafiği — 8 node / 9 kenar, 2274×782 px · **okunabilir, bir kusurla**

Bu sayfa hiç değişmedi: subgraph kaldırma da yön kuralı da yalnız akış diyagramlarına uygulandı,
 bayt bayt aynı.

- Üç kenar stili görsel olarak **net ayrışıyor**: kalın `direct`, ince `contract`, noktalı `event`.
- İhlal adayı hedefleri (`Catalog`, `Inventory`) kalın mor kenarlıkla işaretli, tek bakışta
  seçiliyor.
- Kutu bindirmesi yok, ok yönleri belirsiz değil, `x1`/`x2`/`x4` sayıları okunuyor.
- **Kusur:** `Inventory → Ordering` kenarının `contract x1` etiketi, kavisin tepesinden belirgin
  biçimde **yukarıda** duruyor ve boşlukta yüzüyor gibi görünüyor — hangi kenara ait olduğu ilk
  bakışta anlaşılmıyor. Mermaid'in kendi etiket yerleşimi; kaynakta bir hata yok.
- Küçük bir kesişme: kalın `Shared ==> Catalog` yayı, `Ordering --> Cart` kenarını kesiyor. Kalınlık
  farkı ikisini ayırt etmeye yetiyor.

### 6.3 `POST /api/discovery/search` — 4 node / 3 kenar, 625×335 px · **temiz**

Yön kuralı sonrası `TD` (fan 2) ve GitHub sütununa **1,00× ölçekle**, yani satır içi tam boyutta
sığıyor. Subgraph'lı ilk render 1436×842 idi.

- Kesişme yok, bindirme yok, ok yönleri açık.
- **`&gt;` escape'i görsel olarak doğrulandı:** etiket `HTTP -> HttpEmbeddingService` olarak
  render oluyor. Parse geçmesi bunu kanıtlamıyordu; PNG kanıtlıyor.
- Ham SQL sınırı diyagramın kendisinde görünüyor: `ProductVectorRepository.SearchAsync` kesikli
  kenarlıkla çizilmiş ve hiçbir tabloya bağlanmıyor — "hiçbir veriye dokunmuyor" ile
  "bakılamadı" arasındaki fark burada gözle ayrılıyor. Sayfanın **Bilinen sınırlar** bölümü üç
  `file:line` veriyor (`ProductVectorRepository.cs:26`, `:40`, `:60`).
- Şekiller ayrışıyor: endpoint çift çubuklu (`[[ ]]`), external call bayrak (`> ]`).

### 6.4 mermaid.live bağlantıları

Görsel onay kullanıcıda. Bağlantılar `node:zlib` ile üretildi — mermaid.live durumu
`base64url(deflate(JSON))` olarak fragment'te taşıyor ve `pako.deflate` ile `zlib.deflateSync`
aynı akışı veriyor; **hiçbir paket kurulmadı.** Üretici script scratchpad'de, repoya girmedi.

Bağlantıların açıldığı iddia edilmiyor — üretildikleri doğrulandı, gerisi gözle.

---

## 7. Determinizm kuralları

| Kural | Neden |
|---|---|
| **Üretim zaman damgası yok** | Kendi üretim zamanını içeren artefakt asla byte-identical olamaz; `elapsedMs` Faz 4'te tam bu sebeple `graph.json`'dan çıktı. `NoPageRecordsWhenItWasGenerated` bunu tarıyor |
| Her liste sabit anahtarla sıralı | `StringComparer.Ordinal` |
| Mermaid id'leri üretilir | `n0..nN`, sıralı node id'sinden; etiketteki `{`, `}`, `:`, `/` id'ye asla sızmaz |
| UTF-8 (BOM yok), `\n` | iki makine byte'ta anlaşsın |

Test iki koşuyu değil, **permütasyonu** karşılaştırıyor: aynı graph ters sırada verildiğinde de
aynı baytlar. Bu, "iki koşu tuttu"dan güçlü bir iddia.

---

## 8. Çıktı commit'lenir

`out/` repoda tutulur. Üç sebep:

1. **`git diff` bu fazın ürünü.** `modules/dependencies.md`'nin bir PR'da değişmesi = "bu değişiklik
   yeni bir modül bağımlılığı ekliyor". `.gitignore`'daysa o sinyal hiç doğmaz.
2. **Determinizm bunu mümkün kılıyor.** Gürültülü bir diff commit'lenemezdi.
3. **Onboarding okuyucusu repoda.** GitHub'da tıklayınca render olan diyagram, "önce tool'u kur"
   diyen bir README'den kıyaslanamayacak kadar erişilebilir.

Her dosyanın başında: *"ÜRETİLMİŞ DOSYA — elle düzenlemeyin."*

---

## 9. Açık maddeler

| # | Madde | Durum |
|---|---|---|
| A | **Checkout diyagramının okunabilirliği** (§6.1) — 17 giden oklu hub | **kapandı**: B uygulandı (§9.1, §9.3), yabancı kutu geçişi 12→0 |
| A2 | 6 diyagram GitHub sütununa sığmıyor (yön kuralı 20/25→6/25 indirdi) | **kapandı**: her akış sayfasında koşulsuz mermaid.live bağlantısı (§9.6) |
| B | `Inventory → Ordering` etiketinin kavisten kopuk görünmesi (§6.2) | Mermaid yerleşimi; kaynakta hata yok |
| C | F2, F4, F5, F6, F9, F10 (Faz 3) | `known-limitations.md`'de açık; bu faz yeni bir tanesini kapatmadı |
| D | `graph.json`'ın tek seferlik kanonik yeniden sıralaması | ayrı commit olarak bekliyor |

### 9.1 Checkout okunabilirliği — iki deneme, ölçüldü

Aynı 24 node / 33 kenar, dört yerleşim. Ölçüm SVG'den yapıldı (kenar yolları düzleştirilip
örneklendi), göz kararı değil.

| | boyut | en/boy | kesişen kenar çifti | **yabancı kutu geçişi** | paralel demet çifti | en uzun paralel koşu | toplam kenar uzunluğu |
|---|---|---:|---:|---:|---:|---:|---:|
| **Base** — `LR` + subgraph | 2162×2342 | 0,92 | 4 | **12** | 24 | 1008 px | 25k |
| **A** — `TD` + subgraph | 4819×777 | 6,20 | 5 | **12** | **46** | **2010 px** | **35k** |
| **B** — `LR`, subgraph yok, modül etikette | 2012×2189 | 0,92 | 5 | **0** | **11** | **606 px** | **18k** |
| **A+B** — `TD`, subgraph yok | 4420×707 | 6,25 | 5 | **0** | 33 | 1494 px | 23k |

**Metrik tanımları.** *Yabancı kutu geçişi:* kenarın yolu, ne kaynağının ne hedefinin ait olduğu
bir subgraph dikdörtgeninin içinden geçiyor. *Paralel demet çifti:* iki kenar, ortak uçlarının
40 px dışında, birbirinden 14 px'den yakın seyrederek ≥120 px yol alıyor — "demet" gözlemini
sayıya çeviren ölçü.

**Metrik base'de doğrulandı:** 12 yabancı geçişin listesi, PNG'de gözle görülen ihlallerle
birebir örtüşüyor (`n6→n0`, `n6→n1` `Catalog`'un içinden; altı kenar `Notification`'ın,
iki kenar `Inventory`'nin içinden).

**A tek başına her ölçütte kaybettiriyor:** demet çifti 24→46, en uzun paralel koşu 1008→2010 px,
kenar uzunluğu 25k→35k, ve 6,2:1 en/boy oranı (4819 px genişlik) sayfaya sığmayan bir şerit
üretiyor. Yabancı kutu geçişi 12'de sabit — yön değiştirmek subgraph sorununa dokunmuyor.

**B tek başına her ölçütte kazandırıyor:** yabancı geçiş 12→0 (yapısal — kutu kalmıyor), demet
çifti 24→11, en uzun paralel koşu 1008→606 px, kenar uzunluğu 25k→18k, alan 5,06→4,41 Mpx.
PNG'de fan artık takip edilebilir: her ok hızla ayrışıp kendi satırına iniyor.

**B'nin bedeli:** Q3'teki *"bir okun kutudan çıkması = modül sınırı geçildi"* okuması kayboluyor.
Karşılığında modül her etikette yazılı (`Ordering · ordering.orders`). Bu bir takas, bedava
kazanç değil.

### 9.2 Kesişen etiket sorunu — önceki raporda hata

İkinci durak raporunda `AddAsync → outbox_messages` ile `GetByIdempotencyKeyAsync → orders`
kenarlarının kesiştiğini ve etiket aidiyetinin belirsiz olduğunu yazmıştım. **Ölçüm bunu
çürüttü:** base'de bu iki kenar kesişmiyor, aralarındaki en küçük mesafe **55,7 px**, ve her
etiket kendi kenarının üzerinde (0 px) dururken en yakın yabancı kenar 32 ve 102 px uzakta.
Görüntüden yanlış okumuşum.

Ters yönde bir bulgu çıktı: bu kesişme **A, B ve A+B'de gerçekten oluşuyor** (min mesafe
12,4 / 11,0 / 6,0 px). Yine de etiket aidiyeti dördünde de sağlam — etiket kendi kenarına
≤16 px, en yakın yabancı kenara ≥76 px. **Etiketleri kısaltmak gerekmiyor.**

B'de tek bir marjinal vaka var: `CartRecord` etiketi (`n2→n12`) kendi kenarına 5 px, komşu
kenara 24 px — teknik olarak doğru, ama iki kenarın kesiştiği yere denk geliyor.

### 9.3 B uygulandı — 25 endpoint'in tamamında ölçüm

Deneme el yapımı tek bir `.mmd` üzerindeydi; aşağıdaki sayılar **üretilen 25 dosyanın** SVG'lerinden.
(Küçük sapma bekleniyordu ve oldu: jeneratör node'ları id sırasına göre yazıyor, elle yaptığım
varyant subgraph gruplaması sırasını koruyordu; dagre farklı sıradan farklı yerleşim üretiyor.
checkout demet çifti 11 yerine 15, kesişme 5 yerine 6 çıktı.)

| | önce | sonra |
|---|---:|---:|
| **Yabancı kutu geçişi** (toplam) | **12** | **0** |
| Kesişen kenar çifti (toplam) | 9 | 11 |
| Paralel demet çifti (toplam) | 28 | **16** |
| checkout: demet / en uzun ∥ | 24 / 1008 px | **15 / 606 px** |
| cancel: demet / en uzun ∥ | 4 / 252 px | **1** / 294 px |
| En geniş diyagram | 2162 px | **2012 px** |

Yabancı kutu geçişi **yalnız checkout'ta** vardı; diğer 24 diyagram küçük olduğu için zaten 0'dı.
Kesişme +2 arttı (checkout 4→6, geri kalan sabit) — demet ve kutu geçişindeki kazanımın yanında
kabul edilebilir.

**Dejenere vakalar geçerli Mermaid kaldı:**

```
GET /                      →  flowchart LR + tek node          (1 node, 0 kenar)
GET /api/payment/dev/payments →  n0[[...]] ==>|"Payment"| n1[(...)]  (2 node, 1 kenar)
```

Parse kapısı 26/26 geçiyor.

**En uzun etiket 57 → 69 karakter:**
`Inventory · POST /api/inventory/dev/reservations/{id:guid}/expire-now`

**Kutular genişlemedi.** Mermaid etiketi `·` ayracında sarıyor: modül birinci satıra, ad ikinci
satıra iniyor. checkout'ta en geniş kutu **389 px → 389 px**; ortalama yükseklik 63 → 85 px,
en yüksek 78 → 102 px. Yani bedel yatay değil dikey.

**Ama sayfa genişliği bir bedel ödedi.** GitHub markdown sütunu ~896 px:

| | önce | sonra |
|---|---:|---:|
| 896 px'i aşan diyagram | **8 / 25** | **20 / 25** |
| Ortalama ölçek | 0,89× | **0,69×** |
| En kötü ölçek (checkout) | 0,41× | **0,45×** |

Subgraph'lar node'ları dikey istifliyordu; kutular kalkınca küçük diyagramlar saf LR zincirine
dönüşüp yatayda yayıldı (ör. `GET /api/cart` 665 → 1725 px). **En kötü vaka iyileşti, ortalama
kötüleşti.** Küçük diyagramların satır içi okunurluğu düştü; hiçbiri eski maksimumu aşmıyor ama
çoğu artık ölçekleniyor.

Bu, B kararını geri almaz — karar yabancı kutu geçişinin yapısal olarak sıfırlanmasına dayanıyordu
ve o gerçekleşti. Bedelin kendisi §9.4'te ölçülüp giderildi.

### 9.4 Yön kuralı — değişken node sayısı değil, FAN

§9.3'ün bedeli tek bir sebepten geliyordu: subgraph'lar node'ları dikey istifliyordu, kalkınca
küçük diyagramlar saf `LR` zincirine dönüşüp yatayda yayıldı. Bu tam olarak `TD`'nin iyi olduğu
şey — ve `TD` checkout'ta reddedilmişti çünkü 17 dallı bir hub'da şerit üretiyor. Küçük
diyagramda hub yok.

25 endpoint, iki yön, subgraph'sız, ölçüldü:

| | 896 px'i aşan | ortalama ölçek | en kötü ölçek |
|---|---:|---:|---:|
| Hepsi `LR` | 20 / 25 | 0,69× | 0,45× |
| Hepsi `TD` | **6 / 25** | **0,89×** | **0,20×** |

Hepsi `TD` ortalamayı düzeltiyor ama checkout'u 4420 px'lik bir şeride çevirip en kötü vakayı
0,45×'ten 0,20×'e düşürüyor. Yani sabit yön ikisinde de yanlış; kural gerekiyor.

**Eşik taraması iki değişkenle yapıldı.** İkisi de aynı sonucu veriyor (6/25 aşan, 0,91 ortalama),
ama ayırma kaliteleri farklı:

| Değişken | `TD` kazananların en yükseği | `LR` kazananların en düşüğü | ayrım |
|---|---:|---:|---|
| Node sayısı | **12** (`put /api/catalog/products/{id:guid}`) | **12** (`post .../cancel`) | **çakışıyor** |
| **En geniş fan** (bir node'un giden kenar sayısı) | **6** | **9** | **temiz boşluk** |

Node sayısı **yanlış değişken**: 12 node'lu iki diyagramdan biri `TD` istiyor (fan 4, 1584 px
vs 1918 px), diğeri `LR` istiyor (fan 9, 2315 px vs 1409 px). Aynı boyut, zıt cevap — çünkü
belirleyici olan boyut değil, tek bir node'un kaç yöne dallandığı. Node sayısına eşik koymak,
fan'ın ölçtüğü şeyi tahmin etmek olurdu.

**Kural: en geniş fan ≤ 7 ise `TD`, üstü `LR`.** Eşik gözlenen boşluğun (6 ile 9) ortasına
konuldu; 6, 7 ve 8 bu veri üzerinde birebir aynı sonucu veriyor, orta değer sınıra düşecek yeni
bir endpoint'e karşı en dayanıklısı.

**Üretilen 25 dosyanın gerçek render'ında ölçülen sonuç** (tahmin değil):

| | önce (hepsi LR) | sonra (kural) |
|---|---:|---:|
| 896 px'i aşan | 20 / 25 | **6 / 25** |
| Ortalama ölçek | 0,69× | **0,91×** |
| En kötü ölçek | 0,45× | 0,45× (değişmedi) |
| Yön dağılımı | 25 `LR` | **23 `TD` / 2 `LR`** |

`LR` kalan ikisi tam da beklenenler: `POST /api/ordering/checkout` (fan 17) ve
`POST /api/ordering/orders/{id:guid}/cancel` (fan 9).

#### Tutarlılık bedeli — ve neden kabul edildi

Diyagramlar artık aynı yönde değil; okuyucu sayfadan sayfaya yön değişimi görebiliyor. Bu gerçek
bir kayıp. Kabul gerekçesi üç madde:

1. **Dağılım 23/2.** Kural "karışık yön" üretmiyor, "varsayılan `TD`, iki istisna" üretiyor.
   25 sayfanın 23'ünü gezen okuyucu tek yön görüyor.
2. **İstisna görünür ve kendini açıklıyor.** İki `LR` diyagramı, tek bir node'un onlarca yöne
   dallandığı iki diyagram — okuyucu farkı fark ettiğinde sebebi zaten ekranda.
3. **Karşılığı büyük.** 19 sayfa satır içi okunmaz hâlden okunur hâle geçiyor (20/25 → 6/25).
   Tutarlılık, okunamayan bir sayfada zaten bir şey ifade etmiyor.

Kural `DirectionFollowsTheWidestFanOut` ile iki uçtan sabitlendi: checkout `LR` kalmalı, düz bir
zincir `TD` olmalı. Sessizce ters dönemez.

#### Kalan 6 dosya → §9.6'da kapandı

Kural aşan sayısını 20'den 6'ya indirdi ama sıfırlamadı. Kalanlar 0,45×–0,84× arasında ölçekleniyor
ve satır içi okunmuyor.

### 9.6 Koşulsuz ve iddiasız bir satır — vekil eşikle tahmin yerine

Kalan 6 dosyaya "bu diyagram sığmıyor" notu düşmek cazipti ve **yanlış olurdu**: jeneratörün bir
renderer'ı yok, piksel genişliğini ölçemez. Notu koşullu yapmak, hangi dosyanın sığmadığını bir
vekil değişkenden (node sayısı, fan, etiket uzunluğu) **tahmin etmek** demekti — ölçüm kılığında
bir tahmin, bu projenin bütün fazlarda reddettiği şey. Notu her sayfaya koşulsuz basmak da
19 sayfada olmayan bir sorunu ilan ederdi.

**Karar: koşulsuz ama iddiasız bir satır.** Her akış sayfasının sonunda:

> Diyagram dar görünüyorsa tıklayarak büyütebilir veya
> [mermaid.live'da açabilirsiniz](…).

Cümle hiçbir şey iddia etmiyor — "sığmıyor" demiyor, koşula bağlı değil, vekil değişken
kullanmıyor. 19 sayfada da doğru, 6 sayfada da doğru. Koşulsuz olduğu için determinizmi de
bozmuyor: dallanma yok, her sayfa aynı şekli alıyor.

#### Sıkıştırma elle yazıldı — encoder determinizmi yüzünden

mermaid.live durumu fragment'te `base64url(zlib(json))` olarak taşıyor. `ZLibStream` de geçerli
bir akış üretirdi ama **yeniden üretilebilir** bir akış üretmezdi: çıkardığı baytlar zlib
derlemesine bağlı (.NET 8'den beri zlib-ng), yani aynı graph iki makinede iki farklı bağlantı
verebilir ve 25 akış sayfasının hepsi sahte diff gösterirdi — Faz 4'te `graph.json` için
çözdüğümüz problemin aynısı, bu kez URL'de.

`MermaidLive.ZLib` bu yüzden **stored block** yazıyor: zlib başlığı, `BTYPE=00` bloklar, adler32.
RFC 1950/1951 bunu tam olarak belirliyor; encoder'a bırakılmış hiçbir seçim yok. Determinizm
çalışma zamanından bağımsız.

İkinci tuzak: gövde bağlantıya girmeden **`\n`'e normalize ediliyor**. `AppendLine`
`Environment.NewLine` yazıyor; normalize edilmezse sıkıştırılan baytlar Windows ile Linux'ta
farklı olur ve byte-identical garantisi platformlar arasında kırılır. `DocsSite.Save` da normalize
ediyor ama bağlantı ondan **önce** üretiliyor.

#### Ölçülen bedel

| | en uzun | en kısa | ortalama |
|---|---:|---:|---:|
| **Stored block** (uygulanan) | **2678** (checkout) | 225 (`GET /`) | 831 |
| Deflate seviye 9 (uygulanmadı) | 942 | 195 | 429 |

Stored'ın bedeli en kötü vakada 2,84×, mutlak değer 2678 karakter — pratik URL sınırlarının çok
altında. Bedel kabul edildi çünkü karşılığı çalışma zamanından bağımsız yeniden üretilebilirlik.

> Eğer bir gün URL'ler ~8000'i aşarsa plan: **sabit seviyeli deflate + round-trip testi.**
> Doğruluk encoder determinizmine bağlı olmaz — round-trip testi her koşuda "bu yük gerçekten bu
> diyagramı veriyor" der. Geriye yalnız cross-machine diff riski kalır ve o
> `known-limitations.md`'ye yazılır.

#### Doğrulama sırası: önce tek bağlantı, sonra yayılım

RFC'ye uygunluk gerekli ama yeterli değildi — mermaid.live'ın parser'ının stored block'u ve
base64url alfabesini kabul edeceği **kanıtlanmamıştı**. Yükte 12 adet `-` var; standart base64'te
bunlar `+` olurdu, yani yanlış alfabe sessizce bozulmaz, açılmaz.

O yüzden 25 sayfa üretilmeden **önce** tek bir bağlantı üretilip elle açıldı ve checkout diyagramı
render oldu. Ancak ondan sonra jeneratöre kondu. Doğrulama üç katmanlı:

1. **C# testi** — `EveryFlowPageCarriesALinkThatDecodesBackToItsOwnDiagram`: yük `ZLibStream` ile
   açılıyor ve JSON'un `code` alanı sayfadaki mermaid bloğuyla karşılaştırılıyor. "Geçerli zlib"
   varsayılmıyor, doğrulanıyor.
2. **node ile bağımsız** — `zlib.inflateSync`, 25/25. C#'ın ürettiğini C#'ın doğrulaması yetmez.
3. **Elle** — kullanıcı checkout bağlantısını açtı, diyagram render oldu. C#'ın ürettiği yük,
   açılan prototiple **birebir aynı** (2647 karakter, aynı baytlar).

---

### 9.5 Ölçümlerin karşılaştırılabilirliği — el yapımı varyant ile üretilen çıktı

§9.1'in deneme sayıları el yapımı bir `.mmd` üzerinde alınmıştı ve üretilen dosyadan saptı:
checkout için demet çifti **11 yerine 15**, kesişme **5 yerine 6**.

Sebep teşhis edildi: **jeneratör node'ları id sırasına göre yazıyor**, elle hazırladığım varyant
ise subgraph gruplama sırasını (Cart, Catalog, Inventory, …) koruyordu. dagre yerleşimi bildirim
sırasına duyarlı olduğu için aynı graph iki farklı yerleşim veriyor.

Ders: **yerleşim ölçümleri yalnız jeneratörün gerçekten ürettiği metin üzerinde alınır.** Elle
kurulmuş bir varyant yönü doğru gösterir (B, LR'den iyi), büyüklüğü göstermez. §9.4'ün bütün
sayıları bu yüzden `docs/` altında üretilmiş dosyalardan alındı.

---

## 11. Kaynak sırası — kardeş kutuların sırası neye göre?

### 11.1 Eski sıra alfabetikti, ve ölçülen oranda yanıltıcıydı

Kardeş kutuların soldan sağa sırası **tam nitelikli sembol adına** göreydi — namespace baskın,
kod sırasıyla ilgisiz. Okuyucu soldan sağa okuyup kod sırası sanabilirdi; bazen doğru çıkardı,
garantisi yoktu.

| Popülasyon | Kardeş grubu | Alfabetik ≠ kaynak |
|---|---:|---:|
| Tüm `CALLS` kenarları | 87 | **53 (%61)** |
| Diyagramlarda görünen kardeşler | 36 (18 çözülebildi) | **11 / 18 (%61)** |

`DELETE /api/cart/items/{productId:guid}` tam örnek. `RemoveItemHandler` sırasıyla `:14`,
`:33`, `:34` çağırıyor; alfabetik sıra altı kutuyu **sınıfa göre** gruplayıp
`Caching.Get, Caching.Remove, Caching.Save, Postgres.Get, …` diziyordu. Doğrusu
`Get:14, Get:14, Remove:33, Remove:33, Save:34, Save:34`.

### 11.2 İddia sınırı — üç cümle, üçü de ölçülmüş bir vakadan

Static analysis **çalışma** sırasını bilemez, **kaynak** sırasını bilir. Her akış sayfasında,
diyagramın hemen altında:

> **Numaralar kaynak kodda yazılma sırasıdır**, çalışma sırası değil — koşullu dallar, döngüler
> ve erken `return`'ler ikisini ayırır. **`koşullu` işaretli adımlar hiç koşmayabilir**, ve bir
> `if`/ternary'nin iki dalındaki adımlar birbirini dışlar — ikisi birden koşmaz. Aynı numarayı
> taşıyan kutular **tek bir çağrıdan** gelir.

Üçüncü cümle `RemoveItemHandler.cs:33/34`'ten geliyor: ikisi bir ternary'nin iki dalı, ikisi de
`(koşullu)` işaretli, ve **ikisi birden koşmuyor**. "Sıra farklı olabilir" bunu söylemiyordu.

**Koşullu ölçümü:** 69 numaralı adımın **13'ü (%19)**, 36 grubun **12'si** koşullu bir dalda.
%19 işaretlemeyi ucuz ve bilgilendirici kılıyor — genel uyarıyla yetinmek gerekmedi.
Tespit `invocation.Ancestors()`'ı metot gövdesine kadar yürüyor: ternary, `if`/`else`, `switch`,
`catch`, `?.` ve `&&`/`||`/`??`'nin **sağ** tarafı. Hangi dalın koşacağını söylemiyor — o
data-flow analysis olurdu, kapsam dışı.

### 11.3 ⚠ Ölçüm, sıralı numaralandırmayı çürüttü

İlk tasarım kenarları `1..n` numaralayacaktı. Ölçüm bunu reddetti: **bir çağrı birden fazla kutu
üretiyor.** Ambiguous interface çözümü tek `carts.GetAsync(...)`'i iki implementasyona açıyor;
geçişli daraltma tek çağrıyı hem repository hem tablo kenarına çeviriyor.

`1..n` numaralamak **13 grupta** kaynakta olmayan adım iddia ederdi:

| | numara | gerçek çağrı yeri |
|---|---:|---:|
| checkout · `CheckoutHandler.HandleAsync` | **17** | **11** |
| cancel · `CancelOrderHandler.HandleAsync` | 9 | 5 |
| delete-cart · `RemoveItemHandler.HandleAsync` | 6 | 3 |
| … 10 grup daha | | |

**Kural: numara adım değil, ÇAĞRI YERİ sırasıdır.** Aynı çağrı yerini paylaşan kardeşler aynı
numarayı alır. Tekrarlanan numara kusur değil, bilgi: *"tek çağrı, birden fazla hedef."*
69 adımın **23'ü** birden fazla hedefli.

> **Bir ölçüm düzeltmesi.** Uygulamadan önceki sezgisel tarama *"18 çözülebilen grubun 18'i"*
> demişti; kesin ölçüm **36 grubun 13'ü (%36)**. Sezgisel yöntem grupların yalnız yarısını
> çözebiliyordu ve çözebildikleri tam da paylaşımlı olanlardı — seçim etkisi. Karar değişmiyor
> (13 grup, 17'ye karşı 11 adım yeterince ciddi), ama sayı yanlıştı.

### 11.4 Etiketin yerleşim maliyeti — Faz 5 kazanımı korundu

Üretilen 25 dosyanın **gerçek render'ından** ölçüldü (§9.5'in dersi):

| | §9.4 sonrası | numaralı |
|---|---:|---:|
| **896 px'i aşan** | **6 / 25** | **6 / 25** |
| Ortalama ölçek | 0,91× | **0,91×** |
| Kesişen kenar çifti | 10 | **9** |
| En geniş diyagram | 2012 px | 2077 px (**+%3,2**) |

21 diyagramda artış ≤18 px; kesişme bir **azaldı**. 20/25 → 6/25 kazanımı yerinde.

### 11.5 Kenar durumlar

| Durum | Karar | Ölçülen |
|---|---|---|
| Aynı hedef birden fazla çağrılıyor | Kenar bölünmez; **tüm** çağrı yerleri kenarda durur, sıralama ilkine göre, adım listesi *(ayrıca :55, :179)* yazar | **57 / 512** `CALLS` kenarı |
| Geçiştirilmiş kenar | **İlk hop'un** çağrı yerini taşır — daraltılmış kenar "burada yazılan çağrı oraya ulaşıyor" demek | tümü |
| Çağrı yeri yok | Numarasız; adım listesinde **açık boşluk** olarak yazılır | **73 / 512** kenar, 20 diyagram kenarı |
| Aynı satırda iki çağrı | **Kolon** ayırıyor (`SourceLocation.WithColumn`) | `CreateProductHandler.cs:21` |

`CheckoutHandler` → `GetByIdempotencyKeyAsync`: **36, 55, 179** — üçü de kayıtlı, `:36` koşulsuz,
diğer ikisi koşullu.

**Çağrı yeri olmayan kenar sessizce kaybolmuyor**, sayfada şöyle duruyor:

```
- `cart.carts` — kaynakta bir çağrı ifadesi yok (veri kenarı ya da arayüzden
  implementasyona geçiş), çağrı yeri kaydedilmedi
```

İki sebep var ve ikisi de meşru: arayüz→implementasyon kenarı DI çözümü, tablo kenarı EF
modelinden geliyor. İkisi de kaynakta bir `invocation` değil, ve uydurulmuş bir konum kabul
edilmiş bir boşluktan kötü olurdu.

**Tek adımlı grupta numara basılmıyor.** Yalnız "1" yazan bir ok, okuyucunun arayıp
bulamayacağı bir "2" ima eder. Numaralandırma *"hangi sırayla"* sorusunu cevaplar; tek konumun
sırası yoktur.

### 11.6 İki mutasyon koşusu — ve birincisinin bulduğu şey

**Mutasyon 1 (ilk deneme) testi kırmadı** ve bu bir bulguydu: komşuluk sıralamasını alfabetiğe
döndürmek çıktıyı değiştirmedi, çünkü kardeş sırasını **çıkıştaki son sıralama** üretiyor.
Komşuluk sıralaması yalnız tie-break'in hangi adayı kazandığını etkiliyor (§2'nin dersi). İkisi
ayrı sebeplerle gerekli.

| Mutasyon | Sonuç |
|---|---|
| Çıkış sıralaması → alfabetik | `SiblingsAreOrderedBySourceNotByName` **düştü**: `[14, 33, 34, 14, 33, 34]` — sınıfa göre gruplanmış sıra |
| Numaralandırma → kenar başına (`1..n`) | `NoFlowShowsMoreStepsThanTheSourceHasCallSites` **düştü**, **13 grubu isimle ve sayıyla** bildirerek |

İkinci mutasyonun raporu:

```
13 grup kaynakta olmayan adım gösteriyor:
  POST /api/ordering/checkout · CheckoutHandler.HandleAsync: 17 numara, 11 çağrı yeri
  POST /api/ordering/orders/{id:guid}/cancel · CancelOrderHandler.HandleAsync: 9 numara, 5 çağrı yeri
  DELETE /api/cart/items/{productId:guid} · RemoveItemHandler.HandleAsync: 6 numara, 3 çağrı yeri
  …
```

Faz 5'te lazy continuation'da işe yarayan yöntem: testin kuralı değil, **jeneratörü** koruduğunu
kanıtlıyor.

### 11.7 Graph anlamı değişmedi

`graph.json` şeması büyüdü, içeriği değil: **415 node, 966 kenar** — öncesiyle aynı, kenar kümesi
küme olarak birebir eşit, diagnostics aynı. Eklenen tek şey kenarların taşıdığı konum bilgisi.

`modules/dependencies.md` **bayt bayt değişmedi**.

---

## 10. Sayılar

| | |
|---|---|
| Test | **206**, 0 atlanan, 48 s |
| Üretilen dosya | 37 |
| Determinizm | 37/37 byte-identical |
| Mermaid parse | 26/26 |
| Markdown blok kapısı | 0 ihlal (düzeltme öncesi 10) |
| Yabancı kutu geçişi | 0 (subgraph kaldırıldı, öncesi 12) |
| En büyük diyagram | 24 node (checkout), ham 192, 2012×2189 px |
| Yön | 23 `TD` / 2 `LR` — en geniş fan ≤ 7 kuralı |
| GitHub sütununu aşan | 6 / 25 (kural öncesi 20 / 25) |
| mermaid.live bağlantısı | 25 / 25 akış sayfası, round-trip 25/25 (C# + node) |
| En uzun bağlantı | 2678 karakter (checkout) |
| Ortalama diyagram | 6,4 node |
| Kaynak sırası | 36 kardeş grubu, 69 numaralı adım, 13'ü (%19) koşullu |
| Bir çağrı → çok kutu | 69 adımın 23'ü birden fazla hedefli |
| Çağrı yeri kayıtlı | 439 / 512 `CALLS` kenarı · 57'si birden fazla yerde yazılmış |
