# Bartender audio resources

`BsAudio` bu klasördeki klipleri dosya adıyla yükler. Klip eksikse oyun hata
vermeden sessiz devam eder; yeni bir klibi eklemek için sahne veya prefab referansı
bağlamak gerekmez.

## Oyun sesleri

| Dosya adı | Kullanım |
| --- | --- |
| `SFX_Pour_Loop` | Dökme boyunca kusursuz kısa loop |
| `SFX_Pour_Start` | Akış başlangıcı |
| `SFX_Pour_End` | Akış bitişi |
| `SFX_GlassPickup` | Bardak seçimi |
| `SFX_GlassSet` | Bardağın rafa oturması |
| `SFX_Check` | Sipariş eşleşmesi |
| `SFX_DeliverSlide` | Portal teslimi |
| `SFX_Invalid` | Geçersiz hamle |
| `SFX_Win` | Tur kazanma |
| `SFX_Fail` | Tur kaybetme |

## UI ve harita sesleri

`SFX_ButtonClick`, `SFX_ButtonBack`, `SFX_TabSwitch`, `SFX_SliderTick`,
`SFX_ToggleOn`, `SFX_ToggleOff`, `SFX_LevelNodePop`, `SFX_MapAdvance`.

Arka plan müziği `BGM_Bar_Loop` adıyla eklenir. Unity desteklediği sürece `.wav`,
`.ogg` veya başka bir AudioClip uzantısı kullanılabilir; önemli olan dosya adıdır.

## Kurulu klipler

- `SFX_ButtonClick.wav`: Block Out'ın sakin UI hissine göre tasarlanmış kısa,
  kuru ve hafif aşağı kıvrılan özgün tuş sesi.
- `SFX_GlassPickup.wav`: bardağı seçerken çalan, kullanıcı tarafından seçilmiş tok
  oturma dokunuşu.
- `SFX_GlassSet.wav`: bardak bırakılırken aynı seçilmiş tok dokunuş.
- `SFX_Win.wav`: hızlı ödül kancalı, sıcak saksafon başarı cümlesi.
- `SFX_Fail.wav`: aynı ses ailesinde, hafif Mi minör gölgeli ve tonik çözülmesi
  olmayan nazik başarısızlık cümlesi.
