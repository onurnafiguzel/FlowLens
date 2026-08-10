# FlowLens — Tasarım Kararları

> Bu dosya, **yapmamayı seçtiğimiz** şeyleri ve sebeplerini kaydeder. Bir aracın neyi yapmadığı,
> ne yaptığı kadar tasarımdır — ve gerekçesi yazılı değilse bir sonraki fazda "kolayca eklenebilir"
> diye geri gelir.

---

## D1 — Triage bir rapor üretir; dal açmaz, yama yazmaz, git'e yazmaz

**Karar:** Faz 6'nın çıktısı yalnız bir rapordur. FlowLens otomatik branch açmaz, otomatik düzeltme
yazmaz, PR açmaz ve **hiçbir git yazma işlemi yapmaz.**

Bu, roadmap §5'in kapsam dışı listesinde duruyordu; buraya gerekçesiyle yazılıyor.

### Uygulanış: kural değil, yüzey

"git'e yazmayacağız" bir hatırlama meselesi olarak bırakılmadı. `GitLog` yalnız iki alt komut
çıkarabiliyor — `rev-parse` ve `log` — ve bunlar sabit dizi olarak yazılı. Keyfi bir git çağrısı
kuran hiçbir kod yolu yok, dolayısıyla "yazma yok" **çağrılabilir yüzeyin bir özelliği**, birinin
uyması gereken bir kural değil.

Argümanlar `ProcessStartInfo.ArgumentList`'ten geçiyor; birleştirilmiş bir komut satırı hiç
kurulmuyor, yani boşluk ya da tırnak içeren bir yol çalışan şeyi değiştiremiyor.

### Üç sebep, üçü de bağımsız olarak yeterli

**1. Alert storm'da geri besleme döngüsü.** Hataya kod yazan bir bot, kendi yamasının ürettiği
alert'e de kod yazar. Bir incident sırasında aynı hata dakikada onlarca kez alert üretir; her biri
bir tetikleyicidir. Deterministik bir raporun böyle bir döngüsü yok — aynı girdi aynı metni verir
ve metin hiçbir şeyi tetiklemez. Otomasyonun durması gereken yer, çıktının bir **sonraki girdiyi
üretebildiği** yerdir.

**2. Log'lardaki PII dışarı çıkar.** Bir yığın izi ve exception mesajı müşteri verisi taşıyabilir —
sipariş kimliği, e-posta, bazen doğrudan kullanıcı girdisi. Bir PR açmak bunu kalıcı, aranabilir ve
çoğu kurumda üçüncü tarafa (GitHub) gitmiş hâle getirir; sildiğinde de geçmişte kalır. Rapor
yerelde kalır ve nereye gideceğine insan karar verir.

> Bu, projenin LLM kararının aynı ailesinden: **veri, kurumun sınırının dışına kendiliğinden
> çıkmamalı.** Roadmap §4'ün "büyük şirketler kaynak kodunu harici bir sağlayıcıya göndermek
> istemiyor" gerekçesi burada log'lar için geçerli.

**3. Review edilmemiş yamanın sahte güveni.** Yeşil bir PR "sorun çözüldü" diye okunur. FlowLens'in
bildiği şey ise akışın **yapısı**: hangi endpoint'ten ulaşılıyor, hangi tabloya dokunuluyor, son
kim değiştirdi. Bunların hiçbiri "hata şu satırda ve düzeltmesi bu" demez. Yapı bilgisinden yama
üretmek, aracın bildiğinden fazlasını iddia etmektir — bu projenin her fazda reddettiği şeyin ta
kendisi: **doğru görünen yanlış cevap.**

### Sınır nerede

Rapor şunu der: *"bu hata `NaiveReservationStrategy.cs:37`'de, o satır ham SQL bölgesi, buraya
2 endpoint'ten ulaşılıyor, dosyayı son değiştiren commit `0037b5a`."*

Şunu **demez**: *"düzeltme şu."* Kararı okuyan verir.

---

## D2 — LibGit2Sharp yerine `git` CLI

**Karar:** git geçmişi `Process.Start("git", …)` ile okunuyor; LibGit2Sharp eklenmedi.

Yeni bir NuGet bağımlılığı roadmap kuralı 3'e tabi ("kapsam dışı listesindeki hiçbir teknoloji
önerilmeyecek; gerekli olduğunu düşünüyorsan önce gerekçeni yaz ve sor"). Roadmap Faz 6 zaten
`git log --oneline -5` yazıyor. Tek bir okuma için bir kütüphane **daha az** değil **daha fazla**
yüzey: bir native bağımlılık, bir sürüm ekseni ve yazma API'sine sahip bir nesne modeli getirirdi —
D1'in yapısal garantisini zayıflatan sonuncusu.

**Bedeli ölçüldü ve kabul edildi:** `git` PATH'te yoksa rapor commit'siz üretilir ve çıkış kodu
3 olur (aşağıda). Kütüphane bu bedeli kaldırırdı, ama karşılığında D1'i bir kural hâline
düşürürdü.

---

## D3 — git başarısız olduğunda rapor yine üretilir (çıkış kodu 3)

**Karar:** Repo bulunamazsa, `git` yoksa ya da bir `git log` hata verirse rapor **yine yazılır**,
git bölümü hatayı açıkça söyler ve süreç `ExitIncomplete = 3` ile biter.

Alternatif — hiç rapor yazmamak — elde olan **doğru** cevabı da atardı. Kökler, tablolar ve bilinen
sınırlar yalnız `graph.json`'dan gelir; git'in yokluğu onları geçersiz kılmaz. Bir incident,
doğru bir cevabı ikinci bir kaynak erişilemedi diye çöpe atmak için en kötü an.

Yeni bir çıkış kodu **eklenmedi**: `ExitIncomplete = 3` zaten *"analiz koştu ama bilerek eksik"*
demek. CI'da bu kodu okuyacak biri için tam eşleme `docs/phase-6-notes.md`'de tablo hâlinde.

---

## D4 — Verilmiş bir yol asla sessizce değiştirilmez

**Karar:** `--repo` verilmişse ve o dizin yoksa, FlowLens başka bir kök **denemez**; hata verir ve
denenen yolu yazar.

Faz 4'te `--FlowLens:GraphPath` için verilen kararın aynısı ve aynı sebeple: operatörün adlandırdığı
kaynak bulunamadığında sessizce başkasını okumak, her cevabı sağlıklı gösterip **yanlış repo**
hakkında konuşmak demektir. Faz 4'ün `graphFilePath` alanı tam bu yüzden eklenmişti.

Verilmemişse kök yığın izinden türetilir (mutlak PDB yolu eksi graph'ın göreli yolu) ve rapor
**hangi yolu kullandığını ve nasıl bulduğunu** yazar.
