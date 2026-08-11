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
- [x] Faz 3 — Graph + tablo/kolon
- [x] Faz 4 — Deterministik API (LLM yok) ← *durak noktası: kullanılabilir ürün*
- [x] Faz 5 — Dokümantasyon & görselleştirme (LLM yok)
- [x] Faz 6 — Triage Bot (LLM yok)
- [x] Faz 7 — Eval set (LLM yok)
- [ ] Faz 8 — Doğal dil arayüzü *(opsiyonel, izole proje)* ← **sıradaki**
- [ ] *Faz sonrası:* MCP server · incremental cache · CI entegrasyonu · web arayüzü

> **Faz 4-7'de LLM yok.** Kurumlar kaynak kodunu harici LLM'e göndermek
> istemiyor; LLM'siz çalışan bir tool doğrudan kurulabilir. Gerekçenin
> tamamı roadmap Bölüm 4'te.

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

## Faz 4 — Deterministik API (LLM YOK)

> **Neden bu faz:** Faz 3 zaten analistin sorusunu %100 precision ile
> cevaplıyor; eksik olan tek şey analistin `dotnet run` çalıştırmak
> zorunda olması. Eksik olan tek şey, analistin
> `dotnet run` çalıştırmak zorunda olması. Bu faz onu çözer ve sonunda
> **kullanılabilir bir ürün** çıkar. Faz 5-7 bunun üstüne biner; doğal dil
> katmanı (Faz 8) en sonda ve opsiyonel.
>
> **Performans notu:** `build` ~25s sürüyor (66 proje design-time build).
> Bu API'nin arkasında ASLA çalışmayacak. API sadece `graph.json` okur,
> traversal ~1,5s. Graph üretimi CI'da veya elle, günde bir kez.

### [KEŞİF]

```
@docs/FlowLens-Roadmap.md — Faz 4'ü oku.
@docs/phase-3-summary.md, @docs/phase3-validation.md ve
@docs/known-limitations.md dosyalarını da oku.

Bu fazda LLM YOK. Sadece graph.json üzerinde çalışan bir HTTP API.

Kod yazmadan planını sun:

--- API yüzeyi ---

  GET  /endpoints            25 endpoint: route, HTTP method, modül, konum
                             Opsiyonel ?module= filtresi
  GET  /trace?node=...       forward traversal
                             Tablolar, kolonlar, R/W ayrımı, event köprüleri
  GET  /backward?node=...    bu node'a ne ulaşıyor
                             RootKind'a göre gruplu (endpoint / consumer /
                             background job)
  GET  /tables               16 tablo, şema ve modül ile
  GET  /graph/stats          node/edge sayıları, diagnostics özeti, graph
                             ne zaman üretildi

1. Request/response modelleri ne olacak? /trace ve /backward ortak bir
   response tipi paylaşsın mı? Faz 5'in doküman üreteci ve Faz 8'in /ask'ı
   bu tipleri kullanacak.

2. graph.json ne zaman yüklenecek — startup'ta bir kez mi, her istekte mi?
   Dosya değişirse fark edilecek mi (file watcher)? Bellekte kaç MB tutuyor?

3. Graph yoksa veya bozuksa API nasıl davranacak? Startup'ta fail mi etsin,
   yoksa 503 ile açık bir mesaj mı dönsün?

4. Node id'leri URL'de nasıl taşınacak? "POST /api/ordering/checkout" ve
   "table:ordering.orders" ikisi de node id — encoding stratejin ne?

--- Faz 3'ten gelen girdiler ---

A) BİLİNEN SINIRLAR CEVABA YANSIMALI. Ölçülen: EF içi tablo recall %90,
   EF dışı (raw SQL) %0, kolon recall %83, precision %100.
   Diagnostics "bakamadım" diyebiliyor — bu bilgi kaybolmamalı.
   - Bir akışta raw SQL diagnostic'i varsa response bunu AÇIKÇA taşımalı:
     "Discovery modülünde 4 noktada raw SQL var, o tablolar bu listede yok."
   - Sessizce eksik liste dönmek, %100 precision'ın değerini yok eder.
   Response'ta ayrı bir "limitations" alanı mı olsun?

B) L8 gürültüsü: Result.Success / Error.Validation gibi utility node'lar
   graph'ta "utility" işaretli. Response'ta filtrelensin mi, yoksa
   ?includeUtility=false parametresiyle opsiyonel mi olsun?

C) İkinci sınıf kenarlar (EntityConstruction, SaveChangesWithEntityParameter,
   SaveChangesInterceptor) ve [ambiguous] işaretleri response'a nasıl
   yansıyacak? Faz 3 üç farklı DI şeklinin (dekoratör / koleksiyon
   enjeksiyonu / config seçimi) tek etiketle gösterilmesinin yanlış
   olduğunu ölçtü.

KAPSAM DIŞI: LLM, /ask, web arayüzü, Mermaid (Faz 5), MCP server. Önerme.

--- Kabul kriterleri ---

- Beş endpoint çalışıyor, HİÇBİRİ LLM çağırmıyor ve HİÇBİRİ solution
  yüklemiyor
- /trace?node=POST /api/ordering/checkout → 12 tablo, 62 kolon döndürüyor
- /backward?node=table:ordering.orders → kökler RootKind'a göre gruplu
- Her response'ta: sonuç + her madde için dosya:satır + traverse edilen
  node sayısı + bilinen sınırlar
- Bir istek < 2 saniye
- Graph yoksa açık hata mesajı, sessiz boş liste YOK
- Testler: her endpoint için integration test, graph eksik senaryosu
- Faz 3'ün testleri yeşil kalıyor

Bitince .http dosyası veya curl örnekleriyle beş çağrının çıktısını göster.
```

---

## Faz 5 — Dokümantasyon & görselleştirme

> **Neden burada:** Faz 4'ün API'si var, veri hazır. Bu, projenin en geniş
> kitleye hitap eden çıktısı — ekibe yeni katılan biri "bu akış nereden
> nereye gidiyor" sorusunu kimseye sormadan cevaplayabiliyor. Ve eskimeyen
> bir dokümantasyon, elle yazılan her dokümantasyondan değerli.
> Hâlâ LLM YOK.

### [KEŞİF]

```
@docs/FlowLens-Roadmap.md — Faz 5'i oku.
@docs/known-limitations.md ve @docs/phase-3-summary.md dosyalarını da oku.

Bu fazda LLM YOK. graph.json'dan deterministik olarak markdown ve Mermaid
üretiyoruz.

Kod yazmadan planını sun:

--- Üretilecek çıktılar ---

1. ENDPOINT AKIŞ DİYAGRAMLARI — endpoint başına bir Mermaid flowchart
   Endpoint → Handler → Repository → Table, event köprüleri ayrı kenar
   stiliyle. Node'lara dosya:satır etiketi.

2. MODÜL DOKÜMANTASYONU — modül başına markdown
   Hangi endpoint'ler, hangi tablolar (R/W ayrımıyla), hangi event'ler
   publish/consume ediliyor, hangi modüllere bağımlı, bilinen sınırlar.

3. MODÜL BAĞIMLILIK GRAFİĞİ — tek bir Mermaid diyagram
   Hangi modül hangisine dokunuyor, senkron çağrı mı event mi ayrımıyla.
   Mimari ihlaller burada görünür hale gelir.

4. INDEX — üretilen her şeyi bağlayan bir README

--- Sorular ---

1. Checkout 180 node içeriyor; ham haliyle Mermaid'e dökülürse okunamaz.
   Nasıl daraltacaksın? Seçenekler: utility node filtresi, sadece
   Endpoint/Handler/Repository/Table/Event tiplerini gösterme, derinlik
   sınırı, veya "katman" bazlı gruplama. Kararını gerekçelendir ve
   OKUNABİLİRLİĞİ ölçüt al — 15-25 node'luk bir diyagram hedefle.

2. Mermaid'in GitHub'da render sınırları var (node sayısı, etiket
   uzunluğu, özel karakterler). Route'lardaki {id:guid} gibi süslü
   parantezler ve / karakterleri escape gerektiriyor mu? Test et.

3. Modüller arası event köprüleri diyagramda nasıl gösterilecek —
   subgraph ile modül kutuları mı, farklı kenar stili mi?

4. CLI arayüzü: `flowlens docs -o docs/` tüm çıktıyı üretsin mi, yoksa
   `--endpoint`, `--module` gibi filtreler mi olsun?

5. Üretilen dosyalar repoya commit'lenecek mi, yoksa .gitignore'da mı
   olacak? İkisinin de gerekçesi var — karar ver ve yaz.

--- Kabul kriterleri ---

- `flowlens docs -o out/` çalışıyor, LLM çağırmıyor, solution yüklemiyor
- 25 endpoint için diyagram + 9 modül için doküman + 1 bağımlılık grafiği
- Üretilen Mermaid GitHub'da HATASIZ render oluyor (en az 3 tanesini
  gerçekten GitHub'da veya mermaid.live'da doğrula, ekran çıktısını bildir)
- Her diyagram/doküman dosya:satır referansı taşıyor
- Bilinen sınırlar (raw SQL modülleri) ilgili dokümanda AÇIKÇA yazıyor —
  Discovery dokümanı "bu modülün tabloları görünmüyor, sebebi ham SQL"
  demeli
- Çıktı %100 deterministik: aynı graph.json iki kez çalıştırıldığında
  byte-identical sonuç
- Faz 4'ün testleri yeşil kalıyor

Bitince checkout diyagramını ve Ordering modül dokümanını bana göster.
```

---

## Faz 6 — Triage Bot (deterministik)

### [KEŞİF]

```
@docs/FlowLens-Roadmap.md — Faz 6'yı oku.
@docs/phase-4-notes.md, @docs/phase-5-notes.md ve
@docs/known-limitations.md dosyalarını da oku.

Bu faz mevcut altyapının ters yönde kullanımı, yeni bir sistem değil.
LLM YOK — özet cümlesi bile yazılmayacak, çıktı yapılandırılmış rapor.

--- Faz 4-5'ten devreden girdiler ---

A) BACKWARD ZATEN VAR ve doğrulandı (%100 recall/precision, Faz 3).
   Faz 4'ün AnswerBuilder'ını ve GraphSource'unu AYNEN kullan — üçüncü
   bir doğruluk kaynağı yaratma. Faz 5'in DocsSite'ı da aynı disiplinle
   yazıldı, örnek al.

B) RootKind gruplaması hazır: entryPoints.groups zaten
   Endpoint / Consumer / BackgroundService ayrımını taşıyor. Rapor bunu
   kullanmalı — "4 endpoint" değil "4 endpoint + 1 background job".
   Bu ayrım Faz 4'te tam bu faz için eklendi.

C) callSite bilgisi CALLS kenarlarında var (Faz 5). Stack trace'teki
   satır numarasıyla eşleştirmek mümkün mü, ölç. Mümkünse rapor "hata
   bu akışın 3. adımında" diyebilir — sadece "bu akışta" demekten iyi.

D) limitations mekanizması hazır (Faz 4): diagnostics dosya eşleşmesiyle
   cevaba bağlanıyor. Triage raporu da bunu taşımalı — hata noktası ham
   SQL bölgesindeyse rapor bunu söylemeli.

E) Faz 5'in dersi: sıralamayı deterministik yapmak yetmez, keşfin
   kendisi deterministik olmalı. Rapor çıktısı deterministik olacaksa
   aynı tuzağa dikkat.

Kod yazmadan planını sun:

Akış:
1. Input: stack trace metni (veya exception type + method name)
2. Stack trace'i parse et, proje-içi (ModularCommerce namespace'li) en
   üstteki frame'i bul
3. O symbol'ü graph'ta eşleştir
4. Backward(symbolId) → hangi endpoint / consumer / background job'dan
   ulaşılıyor (RootKind'a göre gruplu)
5. Forward(symbolId) → bu noktadan sonra hangi tablolara dokunuluyor
6. `git log --oneline -5 -- <filePath>` ile ilgili dosyalardaki son
   commit'leri çek
7. Incident report üret

Sorular:
1. Stack trace parse: .NET stack trace formatı, async metotların
   MoveNext() gürültüsü, generic metotlar. Hangi satırları eleyeceksin?
2. Symbol eşleştirme: stack trace'teki isim ile graph node id'si aynı
   formatta değil. Nasıl eşleştireceksin? Eşleşmezse ne olacak?
3. git komutunu nasıl çalıştıracaksın — Process.Start mı, LibGit2Sharp mı?
   Hedef repo yolu nereden gelecek?
4. Çıktı formatı: markdown mı, JSON mı, ikisi de mi?

SINIR — bunları YAPMA:
- Otomatik branch açma
- Otomatik fix yazma
- Herhangi bir git WRITE işlemi (sadece log okuma)
- LLM ile özet yazma

Çıktı bir rapordur. Nedenini docs/design-decisions.md'ye yaz:
alert storm'da loop riski, log'lardaki PII'nin dışarı çıkması, review
edilmemiş patch'in yarattığı sahte güven.

--- Kabul kriterleri ---

- Gerçek bir exception stack trace'i verildiğinde doğru endpoint'leri,
  tabloları ve son commit'leri içeren rapor üretiliyor
- Kökler RootKind'a göre gruplu ("2 endpoint + 1 background job")
- Eşleşmeyen frame'ler sessizce atlanmıyor, raporda listeleniyor
- git komutu başarısız olursa (repo yok, git yok) açık hata
- En az 2 gerçek stack trace ile test edildi
- Faz 5'in testleri yeşil kalıyor
```

---

## Faz 7 — Eval set

> **Bu adım opsiyonel değil.** Faz 3'te 110 test yeşilken graph üç yerde
> sessiz yanlış cevap veriyordu. Testler kodun çalıştığını doğrular;
> eval set cevabın doğru olduğunu doğrular.

### [KEŞİF]

```
@docs/FlowLens-Roadmap.md — Faz 7'yi oku.
@docs/phase3-validation.md — F1..F10 listesini ve §10.3'teki meta-test
şartnamesini oku.
@docs/phase-6-notes.md — "Test doğruydu, popülasyon sessizdi" bölümünü oku.
@docs/known-limitations.md — L1..L20.

Kod yazmadan planını sun. Bu faz LLM kullanmıyor; eval set deterministik
API/CLI üzerinden koşar.

--- POPÜLASYON KURALI (Faz 6'nın dersi, bu fazın merkezi) ---

Faz 6'da bir mutasyon hiçbir testi kırmadı çünkü beş fixture'ın hiçbirinde
o şekli tetikleyecek veri yoktu — graph'ta 310 örnek olmasına rağmen.
Kural: fixture seti bir ÖRNEKLEM, graph POPÜLASYONUN KENDİSİ.

Bu faza uygulanışı: her soru için ÖNCE şunu ölç —
  "bu sorunun yakaladığı hata sınıfından graph'ta kaç örnek var?"

Sınıf tek örnekliyse eval set o KATEGORİYİ değil, yalnız O ÖRNEĞİ ölçüyor.
Soru vakaları graph'tan seçilecek, elde olan fixture'lardan değil.

Planında bu ölçümü göster: hangi kategoriden graph'ta kaç örnek var,
ve seçilen soru o kategorinin temsilcisi mi.

--- ÖNCE CEVAPLA ---

1. Eval set neyi ölçüyor? Aday yüzeyler: CLI trace, HTTP /trace ve
   /backward, AnswerBuilder doğrudan. Hangisi ve neden? Faz 8 yazılırsa
   aynı set /ask üzerinden de koşacak ve AYNI sonucu vermeli — bunu
   şimdiden mümkün kılacak şekilde tasarla.

2. 275 test zaten var. Eval set onları TEKRARLAMAMALI. Farkı tek cümlede
   yaz: test kodun çalıştığını, eval cevabın doğru olduğunu doğrular.
   Hangi soru tipleri mevcut testlerle örtüşüyor, onları ele.

3. expected değerleri nasıl üretilecek? KRİTİK: ModularCommerce kaynak
   kodunu ve Migrations/*.cs'i okuyarak, FlowLens çıktısına BAKMADAN.
   Bu bir test seti, tool'un çıktısının kopyası değil. Süreci tarif et
   ve her soru için hangi dosyaları okuduğunu notes'a yazacağını taahhüt et.

4. Kaç soru? Roadmap 20 diyor. Kategori dağılımı ölçüme göre revize
   edilmeli mi? Zorunlu: en az 2 soru Discovery'den (EF dışı, ham SQL) —
   Faz 3'te bu atlandığı için iki kategori hiç ölçülmedi.

--- METRİKLER ---

- Recall = bulunan doğru / beklenen        ← ÖNCELİKLİ (eksik kolon,
                                              fazla kolondan tehlikeli)
- Precision = bulunan doğru / dönen tüm
- Tablo seviyesi ve kolon seviyesi AYRI
- EF içi ve EF dışı AYRI — tek ortalama aracın nerede kör olduğunu gizler
  (Faz 3: EF içi %90, EF dışı %0, ortalama %82 ikisini de gizliyordu)

--- META-TEST ---

F1..F10 ve L1..L20'nin her biri için: "bu eval set o farkı görünür
kılıyor mu?" tablosu. Kılmayan varsa eval set eksiktir, soru ekle.
Faz 3 §10.3'te bunun şartnamesi zaten yazılı.

--- KABUL KRİTERLERİ ---

- evals/questions.json — her soruda expected + category + notes
  (hangi kaynak dosyalar okundu)
- Runner çalışıyor, sonuç deterministik (aynı graph → aynı rapor)
- evals/report.md — genel + kategori bazlı recall/precision, EF içi/dışı
  ayrı, başarısız her vaka için ne bekleniyordu / ne geldi / neden kaçtı
- Kaçırma nedenleri kategorize: reflection, dynamic dispatch, raw SQL,
  interface ambiguity, inlining, diğer
- Meta-test tablosu dolu
- Popülasyon ölçümü her kategori için raporlanmış
- 275 test yeşil kalıyor, out/ ve graph.json DEĞİŞMİYOR

Recall %100 çıkarsa ŞÜPHELEN — eval set çok kolay demektir, zor vaka ekle.
LLM kullanma, solution yükleyen bir yol önerme.
```

### [UYGULAMA] — plan mode kullanıyorsan gerek yok

```
@docs/FlowLens-Roadmap.md — Faz 7'yi oku.
@docs/phase3-validation.md — F1..F10 listesini ve §10.3'teki meta-test
şartnamesini oku.

1. evals/questions.json oluştur — 20 soru. Her biri:
   {
     "id": "eval-01",
     "question": "Sipariş iptal akışı hangi tabloları etkiliyor?",
     "node": "endpoint:POST /api/ordering/orders/{id:guid}/cancel",
     "direction": "forward",
     "expectedTables": ["ordering.orders", "ordering.order_status_history"],
     "expectedColumns": ["ordering.orders.Status"],
     "category": "ef-in|ef-out|cross-module|ambiguous",
     "notes": "elle doğrulandı, OrderCancelHandler.cs:34 + Migrations/..."
   }

   KRİTİK: expected değerlerini ModularCommerce KAYNAK KODUNU ve
   Migrations/*.cs'i okuyarak çıkar, FlowLens'in çıktısına BAKMADAN.
   Bu bir test seti, tool'un çıktısının kopyası değil. Her soru için
   hangi dosyaları okuduğunu notes'a yaz.

   Dağılım: 12 kolay/orta akış · 4 event üzerinden modül geçen akış ·
   4 zor vaka. ZORUNLU: en az 2 soru Discovery modülünden (EF dışı,
   ham SQL) — Faz 3'te bu atlandığı için iki roadmap kategorisi hiç
   ölçülmemişti.

2. evals/ altında runner yaz — tüm soruları koşup karşılaştıran.

3. Metrikler:
   - Recall = bulunan doğru / beklenen        ← öncelikli metrik
   - Precision = bulunan doğru / dönen tüm
   - Tablo seviyesi ve kolon seviyesi AYRI
   - EF içi ve EF dışı AYRI raporlanacak — tek ortalama aracın nerede
     kör olduğunu gizler

4. evals/report.md:
   - Genel ve kategori bazlı recall/precision
   - Başarısız her vaka: ne bekleniyordu, ne geldi, neden kaçtı
   - Kaçırma nedenleri kategorize: reflection, dynamic dispatch,
     raw SQL, interface ambiguity, diğer

5. META-TEST: F1–F10'un her biri için "bu eval set o farkı görünür
   kılıyor mu" tablosu. Kılmayan varsa eval set eksiktir, soru ekle.

Recall %100 çıkarsa ŞÜPHELEN — eval set çok kolay demektir, zor vaka ekle.
```

---

## Faz 8 — Doğal dil arayüzü (opsiyonel, izole)

> **Neden en sonda:** kurumlar kaynak kodunu harici LLM'e göndermek
> istemiyor; LLM'siz çalışan bir tool doğrudan kurulabilir. Ayrıca LLM
> doğruluğa hiçbir şey katmıyor — Faz 4-7 zaten ölçülmüş bir doğruluk
> veriyor. Bu katman konfor ekliyor ve projenin tezini *gösterilebilir*
> kılıyor.

### [KEŞİF]

```
@docs/FlowLens-Roadmap.md — Faz 8'i ve Bölüm 4'teki izolasyon kuralını oku.
@evals/report.md ve @evals/questions.json — parite tasarımı orada hazır.
@docs/phase-7-notes.md, @docs/known-limitations.md (L21..L24).

Kod yazmadan planını sun.

--- FAZ 7'DEN GELEN GİRDİLER ---

A) PARİTE TASARIMI HAZIR. Her soru question (doğal dil) ve selector
   (oracle çözüm) alanlarını AYRI taşıyor. Faz 8'de:
     question → LLM#1 → selector' → AnswerBuilder → expected
   ve AYRICA selector' ↔ selector karşılaştırılacak.
   Böylece iki hata kaynağı ayrışır:
     - HEDEFLEME: LLM#1 yanlış node seçti
     - AKTARIM:   LLM#2 cevabı bozdu
   Yalnız AnswerBuilder'a koşarsan "aynı sonuç" tautoloji olur ve LLM
   katmanı hiç ölçülmez. Planın bu ayrımı nasıl kuracağını anlat.

B) EVAL SET /ASK ÜZERİNDEN DE KOŞACAK ve deterministik yüzeyle AYNI
   sonucu vermeli. Fark varsa LLM katmanı bilgi kaybediyor demektir.
   22 sorunun tamamı koşulacak, örneklem değil.

C) ÖLÇÜLEN SINIRLAR CEVABA YANSIMALI, uydurulmadan ve gizlenmeden:
   tablo recall EF içi %97,1 / EF dışı %75, kolon-yazma %81,6 / %75,
   kolon-okuma %0, kök %76,5, event %60, dış depo %0.
   L24 kritik: geri sorularda "bu tabloya bakamadığım bir yer var"
   uyarısı YAPISAL OLARAK çıkmıyor. LLM bunu telafi etmeye çalışmasın —
   olmayan bir uyarıyı uydurmak, eksik listeyi tam göstermekten kötü.

D) FAZ 7'NİN DÜRÜSTLÜK MEKANİZMALARI BURADA DA GEÇERLİ: LLM'in
   ürettiği hiçbir şey questions.json'a veya expected değerlere
   dokunamaz. /ask koşusu ayrı bir rapor üretir.

--- Kod yazmadan planını sun ---

--- İZOLASYON (pazarlık konusu değil) ---

- FlowLens.Llm AYRI bir proje olacak
- FlowLens.Core ona referans VERMEYECEK — bağımlılık tek yönlü
- Yapılandırmayla kapatılabilecek; kapalıyken Faz 4-7'nin her şeyi çalışır
- Kapalıyken LLM SDK'sı build'e girmeyecek
- Kodun tamamı LLM'e GÖNDERİLMEYECEK — yalnız kullanıcının sorusu ve
  C# tarafından hazırlanmış dar bir node listesi
- Self-hosted/yerel model kullanılabilir olsun: endpoint ve model adı
  yapılandırmadan gelsin, sağlayıcıya sıkı bağlanma

Planın bu kısıtları nasıl karşıladığını açıkça anlat.

--- Akış ---

1. Soru → LLM #1 → { "target": "...", "direction": "forward|backward" }
2. target → graph'ta fuzzy match → node id            [C# kodu]
3. Faz 4'ün Forward/Backward'ını çağır                [C# kodu]
4. Sonuç → LLM #2 → analiste yazılmış cevap + citations

1. LLM #1 prompt taslağı ve JSON schema. Parse hatası / schema
   uyumsuzluğunda ne olacak?
2. Fuzzy matching stratejisi. Birden fazla yakın eşleşme varsa?
3. LLM #2 prompt taslağı. Her iddianın dosya:satır taşımasını nasıl
   garanti edeceksin? Graph'ta olmayanı söylemesini nasıl engelleyeceksin?
4. Node bütçesi: checkout 180 node döndürüyor. Nasıl daraltacaksın —
   utility filtresi, tablo/kolon özeti, yoksa tam ağaç mı?
5. Eşleşme bulunamazsa akış?
6. Timeout, retry, circuit breaker — hangisi gerekli?
7. Faz 3'ün ölçtüğü sınırlar (raw SQL, F5 outbox kolonları) cevaba nasıl
   yansıyacak? LLM bunları uydurmamalı ama gizlememeli de.

--- Kabul kriterleri ---

- POST /ask çalışıyor
- LLM kapalıyken uygulama açılıyor ve Faz 4-7 endpoint'leri çalışıyor
  (bunun testi VAR)
- FlowLens.Core'un FlowLens.Llm'e referansı YOK (mimari test ile sabitli)
- Response'ta: özet + tablolar/kolonlar + dosya:satır + node sayısı +
  bilinen sınırlar
- LLM #1 çıktısı structured output ile validate ediliyor, invalid ise retry
- Eşleşme bulunamayınca uydurmuyor, "bulamadım" + öneri listesi dönüyor
- API key user secrets'tan okunuyor; kodda ve appsettings.json'da YOK
- Faz 7'nin eval set'i /ask üzerinden de koşuyor ve deterministik API ile
  AYNI sonucu veriyor — fark varsa LLM katmanı bilgi kaybediyor demektir
- Faz 7'nin testleri yeşil kalıyor

Bitince şu üç soruyu dene ve çıktıları göster:
  "İade akışı hangi tablolara ve kolonlara dokunuyor?"
  "ordering.orders tablosuna kim yazıyor?"
  "Ürün aramaya yeni bir filtre eklesek neresi etkilenir?"  (raw SQL vakası)
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