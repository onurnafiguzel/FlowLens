# FlowLens — Claude Code Prompt Seti

**Kullanım:** `FlowLens-Roadmap.md` dosyasını FlowLens reposunun kökünde `docs/` altına koy. Claude Code'u FlowLens repo kökünde aç. Her prompt'ta `@docs/FlowLens-Roadmap.md` referansı geçiyor.

**Genel kural:** Her fazda önce `[KEŞİF]` prompt'unu çalıştır, planı oku, onayla, sonra `[UYGULAMA]` prompt'una geç. Plan mantıklı gelmiyorsa onaylama — düzeltmesini iste.

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

Kod yazmadan planını sun:

1. Entry point olarak hangi endpoint'i seçelim? ModularCommerce'te orta
   karmaşıklıkta, en az bir event publish eden bir akış öner ve neden onu
   seçtiğini söyle.

2. InvocationExpressionSyntax'tan hedef metoda ulaşma stratejin ne?
   GetSymbolInfo'nun Symbol ve CandidateSymbols alanları arasındaki farkı açıkla.

3. Interface problemi: IOrderRepository.Update çağrısı interface symbol'üne
   çözülüyor, concrete implementation'a değil. Roadmap'te önerilen çözüm
   SymbolFinder.FindImplementationsAsync — bunu nasıl uygulayacaksın?
   Birden fazla implementation dönerse ne yapacaksın?

4. Recursion kontrolü: cycle nasıl engellenecek, max depth nasıl yönetilecek?

5. MassTransit: Publish<T>() çağrısındaki generic type argument'ı nasıl
   yakalayacaksın? Karşılık gelen IConsumer<T> nasıl bulunacak?

Planı onaylamamı bekle.
```

### [UYGULAMA]

```
Planı uygula.

Faz 2 kabul kriterleri:
- Seçilen endpoint'ten başlayarak call chain recursive çıkarılıyor
- Konsol çıktısı ağaç formatında, girintili — her satırda dosya:satır
- Publish edilen event ve onu consume eden handler zincire dahil,
  bu geçiş çıktıda açıkça işaretli (örn. "⚡ EVENT: OrderCreated →")
- Ambiguous interface resolution durumunda tüm adaylar listeleniyor ve
  "AMBIGUOUS" olarak işaretleniyor
- Cycle'da sonsuz döngüye girmiyor
- maxDepth parametresi çalışıyor

Test: FlowLens.Tests'e bir integration test ekle — bilinen endpoint için
zincirde belirli bir handler'ın bulunduğunu doğrulayan.

Bitince kabul kriterlerini tek tek doğrula.
```

### [DOĞRULAMA]

```
Çıkardığın call chain'i ModularCommerce'in gerçek kodu ile karşılaştır.

Zincirdeki her adımı kaynak kodda doğrula. Eksik veya fazla bir şey var mı?
Özellikle şunlara bak:
- Atlanmış bir servis çağrısı var mı?
- Zincire girmiş ama gerçekte çağrılmayan bir şey var mı?
- Reflection, dynamic, veya delegate üzerinden yapılan ve yakalayamadığın
  çağrılar var mı?

Bulduğun kayıpları docs/known-limitations.md dosyasına yaz.
```

---

## Faz 3 — Graph + tablo/kolon eşlemesi

### [KEŞİF]

```
@docs/FlowLens-Roadmap.md — Faz 3'ü ve Bölüm 5 (Veri modeli) bölümünü oku.

Kod yazmadan planını sun:

1. Node ve Edge C# modelleri nasıl olacak? Node id stratejisi ne olacak —
   fully qualified symbol name mi, hash mi? Neden?

2. EF Core IModel'e erişim: DbContext'i design-time'da nasıl örnekleyeceksin?
   Veritabanı bağlantısı OLMADAN model metadata'sına erişmenin yolu ne?
   ModularCommerce'te birden fazla DbContext varsa hepsini nasıl toplayacaksın?

3. Bir repository metodunun hangi entity'ye WRITE yaptığını Roslyn'den nasıl
   çıkaracaksın? DbSet<T> erişimi, Add/Update/Remove çağrıları, SaveChanges —
   hangi sinyalleri kullanacaksın?

4. Kolon seviyesi: bir handler'da `order.Status = ...` şeklinde set edilen
   property'yi yakalayıp kolona bağlamanın yolu ne? Bunun sınırları neler?

5. Traversal: Forward ve Backward metotlarının imzaları ne olacak?

ÖNEMLİ: Graph database, vector store veya harici bir bağımlılık ÖNERME.
graph.json + List<Node> + LINQ ile çözülecek. Yetersiz olacağını
düşünüyorsan önce gerekçeni yaz ve bana sor.

Planı onaylamamı bekle.
```

### [UYGULAMA]

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
