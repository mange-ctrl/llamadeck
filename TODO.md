# LlamaDeck – att göra

Backlog för kontrollpanelen. Inget av detta är påbörjat om inget annat står.

## Kluster – dela en stor modell över flera datorer

**Status:** byggd 2026-11-05 i LlamaDeck_beta_kluster.exe + LlamaDeckNode.exe. Testad över loopback; kvar att prova mellan två riktiga datorer.

Idén: en 128B Q4 (~80–85 GB) ska kunna köras över x antal PC i nätverket i stället för att
kräva en maskin med 80 GB minne.

**Slutsats från genomgången**

- Det går i teorin, och llama.cpp har stödet: `ggml-rpc-server.exe` finns redan i varje
  backend-mapp (cpu, vulkan, cuda13, opencl). Servern startas med `--rpc host:port,...` och
  `-ngl` styr hur många lager som hamnar på fjärrnoderna.
- Det som används är **pipeline-parallellism** (lagerdelning), inte tensor-parallellism.
  Tensor-parallellism kräver all-reduce per lager och därmed 100 GbE/InfiniBand.
- **Man vinner minne, inte fart.** Varje token måste igenom alla noder i tur och ordning:
  `tid/token ≈ Σ(nodens arbetstid) + (antal noder × RTT)`.
- Nätverket är inte den stora boven: 16–32 KB aktiveringar per lagergräns, ~0,3 ms per hopp på
  1 GbE. Det som kostar är llama.cpp:s synkrona RPC per lager och att latensen inte kan döljas
  med batch 1.
- Uppskattning för 128B dense Q4 på 4 × (32 GB RAM + 8 GB VRAM): **teoretiskt 3–4 tok/s,
  verkligt 1,5–2,5 tok/s**. MoE med ~10B aktiva kan ge 20–40 tok/s, men då måste experterna
  helst ligga lokalt – annars blir routningen en all-to-all över nätet.
- Praktiska krav: samma llama.cpp-bygge på alla noder, port 50052 öppen, modellfilen helst
  lokalt på varje nod (80 GB över 1 GbE = ~11 min inläsning), 2,5 GbE eller bättre rekommenderat.

**Om vi bygger det**

- [ ] `LaunchPlan` får en `RpcNodes`-lista som läggs till som `--rpc` i `BuildArgs()`.
- [x] Klusterflik: lägg till/ta bort nod, testa anslutning (`/health` på rpc-servern), visa
      varje nods VRAM/RAM och hur många lager som föreslås dit.
- [x] Fördela lager proportionellt mot nodernas uppmätta fart (annars sätter långsammaste
      nod taket – Amdahl).
- [ ] Starta `ggml-rpc-server` på fjärrnoder: kräver agent/PsExec-liknande uppstart, eller att
      användaren startar den manuellt på varje maskin (enklare och säkrare först).
- [x] Varna om versionerna skiljer sig mellan noderna.
- [ ] Tydlig förväntansrad i UI: "detta ger mer minne, inte högre fart".

**Alternativ att titta på i stället**

- Petals – distribuerad inferens över internet (volontärnät).
- exo – heterogena enheter, MLX-baserat.
- vLLM + Ray pipeline-parallellism – serverklass, kräver bättre nät.

## MoE – experter på CPU (klart)

- [x] MoEMode/MoeCpuLayers i config, --cpu-moe och --n-cpu-moe i startkommandot.
- [x] MoE-kort som bara visas för MoE-modeller, med Auto / Delvis / Alla på CPU.
- [ ] Automatisk rekommendation: räkna ut hur många expertlager som får plats i VRAM och föreslå.

## Mindre idéer

- [ ] Hjälp för MoE-modeller: förslag på `--n-cpu-moe` och KV-inställningar när modellen är
      större än VRAM.
- [ ] Testknappen: visa svarstid och tok/s i loggraden, inte bara svaret.
- [ ] Minnesprognos innan start: "modell + KV + buffertar = X GB, tak = Y GB" som en rad i loggen.
