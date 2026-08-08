# FlowLens — Claude Code Prompt Seti

**Kullanım:** `FlowLens-Roadmap.md` dosyasını FlowLens reposunun kökünde `docs/` altına koy. Claude Code'u FlowLens repo kökünde aç. Her prompt'ta `@docs/FlowLens-Roadmap.md` referansı geçiyor.

**Genel kural:** Her fazda önce `[KEŞİF]` prompt'unu çalıştır, planı oku, onayla, sonra `[UYGULAMA]` prompt'una geç. Plan mantıklı gelmiyorsa onaylama — düzeltmesini iste.

## Hangi modda çalıştırmalı

`Shift+Tab` ile mod değiştirilir (default → acceptEdits → plan), aktif mod status bar'da görünür. Tek seferlik kullanmak için prompt başına `/plan` yazmak da yeterli.

| Prompt tipi | Mod | Neden |
|---|---|---|
| `[KEŞİF]` | **plan** | Kaynak kod değiştirilemez, sadece okuma + plan yazma |
| `[UYGULAMA]` | **acceptEdits** | Edit'ler otomatik onaylanır, git diff ile sonradan bakılır |
| `[DOĞRULAMA]`, `[ÖĞRENME]`, `[REVIEW]` | **plan** | Çıktı rapor, kod değil |

`bypassPermissions` kullanma — her fazın sonunda kabul kriterlerini doğrulaman gerekiyor, edit'leri hiç görmeden ilerlersen sonraki fazda hata kaynağını bulmak zorlaşır.

Plan mode'da plan hazır olunca üç seçenek sunulur; **"No, keep planning"** ile düzeltme isteyebilirsin — bu, prompt içindeki "onaylamamı bekle" cümlesinin araç seviyesindeki karşılığı.

> **Önemli:** Plan mode'da planı onayladığın anda Claude Code implementasyona geçer, `[UYGULAMA]` prompt'unu ayrıca vermene gerek kalmaz. Bu durumda `[UYGULAMA]` bloğundaki **kabul kriterlerini `[KEŞİF]` prompt'unun sonuna yapıştır** — yoksa plan kriterlerden habersiz kurulur ve bazı istatistikler raporlanmadan kalır.

## İlerleme

- [x] Faz 0 — Kurulum ve keşif
- [x] Faz 1 — Roslyn'e ısınma
- [x] Faz 2 — Call chain
- [ ] Faz 3 — Graph + tablo/kolon
- [ ] Faz 4 — Analyst Bot
- [ ] Faz 5a — Triage Bot
- [ ] Faz 5b — Eval set

---

## Faz 0 — Kurulum ve keşif

### [KEŞİF]

```
@docs/FlowLens-Roadmap.md dosyasını oku.

Henüz kod yazma. Şu anda yapmanı istediğim tek şey keşif:

1. ModularCommerce repo'su şu yolda: <BURAYA_TAM_YOLU_YAZ>
   Bu repo'yu read-only olarak incele, hiçbir dosyasını değiştirme.

2. Şu soruları cevapla:
   - Solution ve proje yapısı nasıl? Kaç modül var, isimleri ne?
   - Endpoint'ler nasıl tanımlanmış — Controller mı, Minimal API mi, ikisi karışık mı?
   - MediatR kullanılıyor mu? Kullanılıyorsa command/query handler'lar hangi
     namespace pattern'ini takip ediyor?
   - EF Core DbContext'ler nerede? Modül başına ayrı DbContext mi, tek DbContext mi?
   - Design-time DbContext factory var mı?
   - MassTransit event contract'ları hangi projede duruyor? Publish ve
     IConsumer<T> kullanım örneklerinden 2-3 tane göster.
   - Repository pattern var mı, yoksa DbContext doğrudan handler'da mı kullanılıyor?

3. Her cevapta somut dosya yolu ve satır numarası ver. Tahmin etme,
   bulamadıysan "bulamadım" de.

Çıktıyı docs/modularcommerce-survey.md olarak kaydet.
```

### [UYGULAMA]

```
Şimdi FlowLens projesini kur:

- .NET 10 console application, adı FlowLens.Cli
- Ayrıca FlowLens.Core adında bir class library (extraction ve graph mantığı buraya)
- FlowLens.Tests — xUnit
- Solution: FlowLens.sln

Paketler (FlowLens.Core'a):
- Microsoft.CodeAnalysis.CSharp.Workspaces
- Microsoft.Build.Locator

Analiz edilecek solution yolunu appsettings.json'dan veya command line
argümanından okuyacak şekilde ayarla — hardcode etme.

.gitignore ekle. README.md'ye tek paragraf: bu projenin ne olduğu.

Kod yaz ama henüz Roslyn kullanma — sadece iskelet ve "Hello" seviyesinde
çalışan bir Program.cs.
```

---

## Faz 1 — Roslyn'e ısınma

### [KEŞİF]

```
@docs/FlowLens-Roadmap.md — Faz 1'i oku.

Kod yazmadan önce planını sun:

- MSBuildWorkspace ile solution yüklemenin doğru sırası ne? MSBuildLocator
  çağrısının nerede olması gerektiğini ve neden orada olması gerektiğini açıkla.
- Solution yüklenirken proje bazlı hata olabilir. Bunu nasıl yakalayıp
  raporlayacaksın?
- Hangi sınıfları oluşturacaksın, sorumlulukları ne?

Planı onaylamamı bekle.
```

### [UYGULAMA]

```
Planı uygula.

Faz 1 kabul kriterleri:
- ModularCommerce.sln hatasız yükleniyor
- Tüm MethodDeclarationSyntax'lar geziliyor
- Konsol çıktısı: "dosyaYolu:satır → metotAdı" formatında
- Sonda özet: toplam proje sayısı, toplam dosya sayısı, toplam metot sayısı,
  modül başına metot sayısı
- WorkspaceFailed event'leri yakalanıp uyarı olarak basılıyor

Ayrıca kısa bir demo yaz: bir MethodDeclarationSyntax alıp aynı metot için
hem SyntaxTree'den hem SemanticModel'den bilgi çıkar, ikisinin farkını
konsola bas. Bu benim öğrenmem için — kodda yorum satırıyla açıkla.

Bitince kabul kriterlerini tek tek doğrula ve sonucu raporla.
```

### [ÖĞRENME] — implementasyondan sonra çalıştır

```
Yazdığın Faz 1 kodunu bana öğretir gibi anlat:

- SyntaxNode, SyntaxToken, ISymbol, SemanticModel, Compilation arasındaki
  ilişki nedir? Somut olarak bizim kodumuzdaki hangi satırda hangisi kullanılıyor?
- Neden SemanticModel document bazlı alınıyor, Compilation bazlı değil?
- Bu API'lerin performans karakteristiği ne — neye dikkat etmem lazım?

Kısa tut, kod referanslarıyla.
```

---

## Faz 2 — Tek endpoint'in call chain'i

### [KEŞİF]

```
@docs/FlowLens-Roadmap.md — Faz 2'yi oku.
@docs/modularcommerce-survey.md — Faz 0 keşif çıktısını da oku.

@docs/known-limitations.md ve @docs/phase-1-notes.md dosyalarını da oku —
Faz 1'in ölçümleri bu fazın tasarımını doğrudan belirliyor.

Kod yazmadan planını sun. Faz 1'den gelen üç bulgu plana dahil edilecek:

--- Faz 1 bulguları ---

A) L1 BLOKE EDİCİ: 24 endpoint'in tamamı Minimal API lambda'sı.
   MethodDeclarationSyntax bunları görmüyor, yani şu an sayılan 399 metodun
   hiçbiri endpoint değil. Planın ilk maddesi bu olsun.
   Somut sorular:
   - MapPost("/orders", async (...) => ...) çağrısından HTTP method ve
     route'u nasıl çıkaracaksın?
   - MapGroup ile prefix varsa tam path'i nasıl birleştireceksin?
   - Lambda gövdesindeki invocation'lardan handler'a nasıl geçeceksin?
   - Endpoint kaydı ayrı bir extension method'da yapılıyorsa (ör.
     MapOrderingEndpoints) bunu nasıl bulacaksın?

B) Interface resolution kenar durum değil, ANA YOL: Faz 1 ölçümünde
   CheckoutHandler'daki 49 invocation'ın 49'u çözüldü ama 10'u interface
   üyesine bağlandı. Buna göre tasarla:
   - SymbolFinder.FindImplementationsAsync maliyeti ne? 66 projede her
     interface çağrısı için ayrı çağrı pahalı olabilir — ölç veya tahmin et.
   - Cache'leyecek misin? Cache key ne olacak?
   - AMBIGUOUS durumunda politika ne? Üç seçenek var, hangisini seçtiğini
     ve neden seçtiğini yaz:
       (i)   tüm implementation'ları ekle
       (ii)  sadece aynı modüldekini ekle
       (iii) DI registration'larını (AddScoped<IX, X>) okuyup gerçek
             implementasyonu bul
     Şimdilik (ii) yeterli olabilir, (iii) en doğrusu ama en pahalısı.
     Kararını gerekçelendir, sonradan değiştirilebilir bırak.

C) Entry point: Ordering modülündeki checkout akışını öner — Faz 1'de
   zaten kısmen ölçüldü, karşılaştırma zemini var. Daha uygun bir akış
   varsa gerekçesini yaz.

--- Standart sorular ---

1. InvocationExpressionSyntax'tan hedef metoda ulaşma stratejin ne?
   GetSymbolInfo'nun Symbol ve CandidateSymbols alanları arasındaki farkı
   açıkla — Faz 1'de 49/49 çözüldü, hangi durumda CandidateSymbols devreye
   girer?

2. Recursion kontrolü: cycle nasıl engellenecek, max depth nasıl yönetilecek?
   Aynı metot farklı yollardan geliniyorsa iki kez mi işlenecek?

3. MassTransit: Publish<T>() çağrısındaki generic type argument'ı nasıl
   yakalayacaksın? Karşılık gelen IConsumer<T> nasıl bulunacak? Bir event'i
   birden fazla consumer dinliyorsa hepsi zincire girecek mi?

4. Performans: Faz 1'de solution yükleme 17.3s, tek proje compilation 4.7s.
   Faz 2'de kaç Compilation'a ihtiyaç var? Tümü mü, lazy mi? Ölçülebilir
   bir hedef koy.

5. Hangi sınıfları oluşturacaksın, sorumlulukları ne? Faz 1'in mevcut
   sınıflarından hangilerini yeniden kullanacaksın?
```

### [UYGULAMA]

```
Planı uygula.

Faz 2 kabul kriterleri:
- 24 endpoint'in tamamı lambda'lardan çıkarılıyor, her biri için HTTP method
  ve tam route (MapGroup prefix'i dahil) doğru — sayı 24'ten azsa neden
  eksik olduğu raporlanıyor
- Seçilen endpoint'ten başlayarak call chain recursive çıkarılıyor
- Konsol çıktısı ağaç formatında, girintili — her satırda dosya:satır
- Publish edilen event ve onu consume eden handler zincire dahil,
  bu geçiş çıktıda açıkça işaretli (örn. "⚡ EVENT: OrderCreated →")
- AMBIGUOUS resolution durumunda seçilen politika uygulanıyor, atlanan
  adaylar da çıktıda görünüyor (sessizce düşürülmüyor)
- Cycle'da sonsuz döngüye girmiyor
- maxDepth parametresi çalışıyor
- Çalışma süresi ölçülüp raporlanıyor

Test: FlowLens.Tests'e integration test ekle —
- Bilinen endpoint için zincirde belirli bir handler'ın bulunduğunu doğrulayan
- Endpoint sayısının .sln'den runtime'da bulunan sayıyla tutarlı olduğunu
  doğrulayan (24'ü hardcode etme)
- Event geçişinin (publish → consumer) zincirde yer aldığını doğrulayan

Bitince kabul kriterlerini tek tek doğrula ve şu istatistikleri raporla:
toplam invocation, çözülen / çözülemeyen, interface üyesine bağlanan sayı,
ambiguous sayısı.

Kaçırdığın veya çözemediğin her şeyi docs/known-limitations.md'ye ekle.
```

### [DOĞRULAMA]

> Bu prompt eksik kalan kabul kriteri raporlamasını da kapsıyor — plan mode
> kullanıldığında `[UYGULAMA]` prompt'u ayrıca verilmediği için bazı
> istatistikler raporlanmadan kalabiliyor.

```
@docs/FlowLens-Roadmap.md — Faz 2 doğrulamasını yap.

Önce raporda eksik kalan üç şeyi ver:

1) Endpoint sayısı uyuşmazlığı: survey 24 diyor, sen 25 route buldun,
   ayrıca L7'de 2 health check endpoint'i eksik. Net tablo çıkar:
   - Kaç MapX invocation bulundu
   - Kaçı sembol doğrulamasından geçti
   - Kaçı elendi (sebebiyle)
   - Survey'deki 24 hangisi — survey mi yanlıştı, sen mi fazla buldun?
   Faz 1'de survey'in 68 dediği yerde 66 çıkmıştı; aynı disiplinle çöz.

2) Invocation istatistikleri: toplam invocation, çözülen / çözülemeyen,
   interface üyesine bağlanan, ambiguous sayısı, CandidateSymbols'a düşen
   var mı.

3) Performans: plandaki hedeflere göre gerçekleşen süreler
   (solution yükleme ≤20s, endpoint keşfi ≤5s, checkout zinciri ≤30s,
   toplam ≤60s). Tutmadıysa nerede.

Sonra asıl doğrulama:

POST /api/ordering/checkout zincirini ModularCommerce'in GERÇEK kodunu
okuyarak elle takip et ve FlowLens çıktısıyla karşılaştır. Her adımı
kaynak kodda doğrula:
- Atlanan bir servis çağrısı var mı?
- Zincire girmiş ama gerçekte çağrılmayan bir şey var mı?
- OrderPaid köprüsü gerçekten doğru mu — raise noktası, mapper eşlemesi
  ve consumer üçü de kaynak kodda teyit edildi mi?
- Reflection, dynamic veya delegate üzerinden yapılan, yakalayamadığın
  çağrılar var mı?

Aynısını GET /api/catalog/products için de yap (kontrast vakası).

Sonucu docs/phase2-validation.md olarak kaydet. Fark varsa nedenini
analiz et: düzeltilebilir bug mı, yoksa static analysis'in yapısal
sınırı mı? Yapısal sınırsa docs/known-limitations.md'ye ekle.
```

---

## Faz 3 — Graph + tablo/kolon eşlemesi

### [KEŞİF]

```
@docs/FlowLens-Roadmap.md — Faz 3'ü ve Bölüm 5 (Veri modeli) bölümünü oku.
@docs/known-limitations.md, @docs/phase-2-notes.md ve
@docs/phase2-validation.md dosyalarını da oku — Faz 2'nin bulguları bu
fazın tasarımını doğrudan belirliyor.

Kod yazmadan planını sun:

--- Standart sorular ---

1. Node ve Edge C# modelleri nasıl olacak? Faz 2'de sabitlenen node ID
   formatı graph.json'da aynen kullanılacak mı, yoksa dönüşüm gerekiyor mu?

2. EF Core IModel'e erişim: DbContext'i design-time'da nasıl örnekleyeceksin?
   Veritabanı bağlantısı OLMADAN model metadata'sına erişmenin yolu ne?
   ModularCommerce'te modül başına ayrı DbContext varsa hepsini nasıl
   bulup yükleyeceksin? Bir DbContext örneklenemezse ne olacak — sessizce
   atlamak YASAK, açıkça raporlanacak.

3. Bir repository metodunun hangi entity'ye WRITE yaptığını Roslyn'den nasıl
   çıkaracaksın? DbSet<T> erişimi, Add/Update/Remove çağrıları, SaveChanges —
   hangi sinyalleri kullanacaksın? IOrderRepository.AddAsync çağrısından
   Order entity'sine, oradan orders tablosuna giden zincirin HER halkasını
   açıkça tarif et. Bu zincir bulanık kalırsa graph'taki tablolar
   "tesadüfen doğru" kategorisine düşer.

4. Kolon seviyesi: bir handler'da `order.Status = ...` şeklinde set edilen
   property'yi yakalayıp kolona bağlamanın yolu ne? Aggregate metotları
   içinden set ediliyorsa (MarkPaid gibi) nasıl izleyeceksin? Bunun
   sınırları neler?

5. Traversal: Forward ve Backward metotlarının imzaları ne olacak?
   Faz 2'nin CallGraphWalker'ı yeniden kullanılacak mı, yoksa graph.json
   üzerinde ayrı bir traversal mı yazılacak?

--- Faz 2'den gelen girdiler ---

A) L9 kararı ŞİMDİ verilecek: constructor çağrıları (new Order(...)) kenar
   üretmiyor. Faz 2'de kaybı yoktu, Faz 3'te entity construction önem
   kazanabilir. Karar kriteri:
   - Entity yazma noktalarını repository çağrılarından (AddAsync, Update,
     SaveChanges) çıkarmak yeterli mi?
   - Yeterliyse L9 kalıcı sınır olarak kapansın.
   - Değilse SADECE EF Core IModel'de karşılığı olan tiplerin
     construction'larını gez — tüm ObjectCreationExpressionSyntax'ı değil.
   Kararını ölçümle gerekçelendir, tahminle değil.

B) L8 gürültüsü: catalog trace'inde 9 düğümün 4'ü Result.Success /
   Result.Failure / Error.Validation gibi Shared.Kernel yardımcıları.
   graph.json'da bunları nasıl ele alacaksın — node olarak kalsınlar mı,
   filtrelensinler mi, yoksa "utility" olarak etiketlenip traversal'da
   opsiyonel mi olsunlar? Faz 4'te LLM'e gönderilecek alt kümenin boyutu
   bu karara bağlı.

C) "Tesadüfen doğru" riski: Faz 2 doğrulamasında NotificationProcessor'ın
   koleksiyon enjeksiyonu anlaşılmadığı halde doğru sonuç çıkmıştı.
   EF Core eşlemesinde aynı tuzağa düşme — bir tablonun graph'a doğru
   sebeple mi yoksa tesadüfen mi girdiğini ayırt edebilecek şekilde kanıt
   taşı (hangi repository çağrısından, hangi entity üzerinden).

D) FakePspClient: ödeme sağlayıcısının sahte implementasyonu. Faz 3'te
   ExternalCall node'u olarak mı ele alınacak, yoksa test double olarak mı?
   "Bu akış hangi dış servise gidiyor" sorusunun cevabı buna bağlı.
   Kararını ve gerekçesini yaz.

--- Kabul kriterleri (plan bunlara göre kurulacak) ---

- graph.json üretiliyor, şeması roadmap Bölüm 5'teki gibi
- HER node'da filePath ve line dolu — boş olan varsa build fail etsin
- Entity → Table ve Property → Column eşlemeleri EF Core IModel'den geliyor
  (isim tahmini veya SQL parse YOK)
- Forward(nodeId, maxDepth) ve Backward(nodeId, maxDepth) çalışıyor
- CLI: `flowlens build` graph üretiyor,
  `flowlens trace <endpoint>` forward traversal sonucunu basıyor
- Graph istatistikleri raporlanıyor: node tipi başına sayı, edge tipi
  başına sayı, ambiguous node sayısı, çalışma süresi
- Faz 2'nin testleri yeşil kalıyor
- Yeni testler: bilinen endpoint için beklenen tablolar graph'ta ·
  backward traversal doğru endpoint'leri buluyor · sabit sayı hardcode yok

ÖNEMLİ: Graph database, vector store veya harici bir bağımlılık ÖNERME.
graph.json + List<Node> + LINQ ile çözülecek. Yetersiz olacağını
düşünüyorsan önce gerekçeni yaz ve bana sor.
```

### [UYGULAMA] — plan mode kullanıyorsan gerek yok

> Kabul kriterleri yukarıdaki `[KEŞİF]` prompt'una dahil edildi. Bu blok
> yalnız edit mode ile çalışıyorsan kullanılır.

```
Planı uygula.

Faz 3 kabul kriterleri:
- graph.json üretiliyor, şeması roadmap Bölüm 5'teki gibi
- HER node'da filePath ve line dolu — boş olan varsa build fail etsin
- Entity → Table ve Property → Column eşlemeleri EF Core IModel'den geliyor
  (isim tahmini veya SQL parse YOK)
- Forward(nodeId, maxDepth) ve Backward(nodeId, maxDepth) çalışıyor
- CLI komutu: `flowlens build` graph'ı üretiyor,
  `flowlens trace <endpoint>` forward traversal sonucunu basıyor

Test:
- Bilinen bir endpoint için beklenen tabloların graph'ta olduğunu doğrulayan test
- Backward traversal'ın doğru endpoint'leri bulduğunu doğrulayan test

Bitince kabul kriterlerini doğrula ve graph istatistiklerini raporla:
node tipi başına sayı, edge tipi başına sayı, ambiguous node sayısı.
```

### [DOĞRULAMA] — bu adımı atlama

```
3 farklı endpoint seç ve her biri için:

1. flowlens trace çıktısını al
2. ModularCommerce kodunu elle takip ederek gerçek etkilenen tabloları/kolonları
   çıkar
3. İkisini karşılaştır, farkları listele

Sonucu docs/phase3-validation.md olarak kaydet. Fark varsa nedenini analiz et
ve düzeltilebilir mi, yoksa yapısal bir sınır mı olduğunu belirt.
```

---

## Faz 4 — Analyst Bot

### [KEŞİF]

```
@docs/FlowLens-Roadmap.md — Faz 4'ü oku.

Kod yazmadan planını sun:

1. Minimal API yapısı: endpoint'ler, request/response modelleri
2. LLM #1 (soru → parametre): prompt taslağını yaz, JSON schema'yı tanımla.
   Parse hatası veya schema uyumsuzluğunda ne olacak?
3. Fuzzy matching: LLM'in döndürdüğü hedef adını graph'taki node'larla
   eşleştirme stratejin ne? Birden fazla yakın eşleşme varsa?
4. LLM #2 (sonuç → cevap): prompt taslağını yaz. Her iddianın dosya:satır
   referansı taşımasını nasıl garanti edeceksin? Graph'ta olmayan bir şey
   söylemesini nasıl engelleyeceksin?
5. Hiç eşleşme bulunamazsa akış ne olacak?
6. Resilience: LLM çağrısı için timeout, retry, circuit breaker — hangisi
   gerekli, nasıl uygulayacaksın?

KRİTİK KURAL: LLM'e graph.json'ın tamamını verme. LLM sadece (a) soruyu
parametreye çevirir, (b) C# tarafından hazırlanmış dar bir node listesini
özetler. Doğruluk C# kodunda üretilir.

Planı onaylamamı bekle.
```

### [UYGULAMA]

```
Planı uygula.

Faz 4 kabul kriterleri:
- POST /ask { "question": "..." } çalışıyor
- Response'ta: özet metin + etkilenen tablolar/kolonlar listesi + her madde
  için dosya:satır referansı + traverse edilen node sayısı
- LLM #1 çıktısı structured output ile validate ediliyor, invalid ise retry
- Eşleşme bulunamayınca uydurmuyor, "bulamadım" + öneri listesi dönüyor
- API key configuration'dan okunuyor, kodda hardcode YOK
- LLM çağrılarında timeout ve retry var

Test:
- LLM'i mock'layarak traversal ve response birleştirme mantığını test et
- Eşleşme bulunamama senaryosunu test et

Bitince şu soruyu gerçek sistemde dene ve çıktıyı göster:
"İade akışı hangi tablolara ve kolonlara dokunuyor?"
```

---

## Faz 5a — Triage Bot

### [KEŞİF + UYGULAMA birlikte]

```
@docs/FlowLens-Roadmap.md — Faz 5a'yı oku.

Bu faz mevcut altyapının ters yönde kullanımı, yeni bir sistem değil.
Önce kısa bir plan sun, sonra uygula:

Akış:
1. Input: stack trace metni (veya exception type + method name)
2. Stack trace'i parse et, proje-içi (ModularCommerce namespace'li) en üstteki
   frame'i bul
3. O symbol'ü graph'ta eşleştir
4. Backward(symbolId) → bu metoda hangi endpoint'lerden ulaşılıyor
5. İlgili dosyalar için `git log --oneline -5 -- <filePath>` çalıştır
6. Incident report üret

Report içeriği:
- Hata konumu (dosya:satır)
- Etkilenen endpoint'ler ve akışlar
- Bu akıştaki tablo/kolonlar
- İlgili dosyalardaki son 5 commit, tarih ve yazarla
- LLM ile yazılmış kısa özet: "muhtemel şüpheli" değerlendirmesi

SINIR — bunları YAPMA:
- Otomatik branch açma
- Otomatik fix yazma
- Herhangi bir git write işlemi

Çıktı bir rapordur. Nedenini docs/design-decisions.md'ye yaz:
alert storm'da loop riski, log'lardaki PII'nin LLM'e gitmesi, review
edilmemiş patch'in yarattığı sahte güven.

Kabul kriteri: gerçek bir exception stack trace'i verildiğinde doğru
endpoint'leri ve son commit'leri içeren rapor üretiliyor.
```

---

## Faz 5b — Eval set

### [UYGULAMA]

```
@docs/FlowLens-Roadmap.md — Faz 5b'yi oku.

Bu adım opsiyonel değil. Bu olmadan tool'un çalıştığını iddia edemem.

1. evals/questions.json oluştur — 20 soru. Her biri:
   {
     "id": "eval-01",
     "question": "Sipariş iptal akışı hangi tabloları etkiliyor?",
     "expectedTables": ["orders", "order_items", "outbox_messages"],
     "expectedColumns": ["orders.status", "orders.cancelled_at"],
     "notes": "elle doğrulandı, OrderCancelCommandHandler:34"
   }

   ÖNEMLİ: expected değerleri sen ModularCommerce kodunu okuyarak çıkar,
   ama FlowLens'in çıktısına BAKMADAN. Bu bir test seti, tool'un çıktısının
   kopyası değil. Her soru için hangi kod dosyalarını okuduğunu notes'a yaz.

   Soru dağılımı: 12 kolay/orta akış, 4 event üzerinden modül geçen akış,
   4 zor vaka (interface ambiguity, dinamik çağrı içeren).

2. evals/run.cs — tüm soruları çalıştırıp karşılaştıran runner yaz.

3. Metrikler:
   - Recall = bulunan doğru tablo / beklenen tablo   ← öncelikli metrik
   - Precision = bulunan doğru tablo / dönen tüm tablo
   - Tablo seviyesi ve kolon seviyesi ayrı raporlanacak

4. evals/report.md — sonuç raporu:
   - Genel recall/precision
   - Başarısız her vaka için: ne bekleniyordu, ne geldi, neden kaçtı
   - Kaçırma nedenleri kategorize edilecek: reflection, dynamic dispatch,
     string-based SQL, interface ambiguity, diğer

Recall %100 çıkarsa şüphelen — eval set'in çok kolay demektir, zor vaka ekle.
```

---

## Yardımcı prompt'lar

### Takıldığında

```
Şu hatayı alıyorum:

<HATA METNİ>

Tahmin yürütme. Önce ilgili kodu oku, hatanın kök nedenini bul, sonra
düzeltme öner. Neden olduğunu da açıkla — aynı hatayı tekrar yapmak
istemiyorum.
```

### Faz sonu review

```
Faz <N>'i bitirdik. Şimdi eleştirel bir review yap:

1. Kabul kriterlerinin her birini tek tek doğrula — gerçekten karşılandı mı?
2. Yazdığımız kodda teknik borç var mı? Varsa listele ve önceliklendir.
3. Roadmap'in "Kapsam Dışı" listesine uymayan bir şey ekledik mi?
4. Bir sonraki faza geçmeden düzeltilmesi gereken bir şey var mı?

Övme, gerçek sorunları söyle.
```

### Anlatım hazırlığı (mülakat / blog için)

```
Faz <N>'de yaptığımız işi iki farklı şekilde anlat:

1. Teknik bir mülakatçıya, 3 dakikada: hangi problemi çözdük, hangi
   trade-off'ları yaptık, sınırları ne.
2. Bir blog yazısının outline'ı olarak: hangi başlıklar, hangi kod
   parçaları, okuyucunun ne öğreneceği.

Abartma. Bu projenin gerçekten ne yaptığını ve ne yapmadığını dürüstçe yaz.
```

### Kapsam kayması olduğunda

```
Dur. Önerdiğin <X> roadmap'in "Kapsam Dışı" listesinde.

Gerçekten gerekli olduğunu düşünüyorsan:
- Hangi somut problemi çözüyor?
- Mevcut basit yaklaşımla neden çözülemiyor?
- Eklemenin maliyeti ne?

Bunları yazmadan implementasyona geçme.
```