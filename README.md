# Mycroft — Motor Assíncrono de Reconciliação Financeira via SLM

Microsserviço open source (.NET 9) que transforma descrições caóticas de extratos, faturas e arquivos CNAB
(`PGTO*IFOOD*SAOPAULO*BR`, `COMPRA ZEMERCADINHO 45,90 02/10`) em transações estruturadas e categorizadas,
usando um **Small Language Model rodando 100% local via Ollama**: nenhum dado sai da sua rede.

```
POST /batch | /upload ──► PostgreSQL (Inbox) ──► Channel<Guid> ──► ReconciliationWorker
         202 Accepted                                     │
                                                          ▼
              parse (CSV/OFX/CNAB/PDF) → normaliza → Alias do Job → Cache global → SLM (chunks)
                                                                                    │ fallback:
                                                                    repair → item a item → regex
                                                          ▼
                                     GET /reconciliations/{id}  +  Webhook HMAC-SHA256 com retry
```

## Subindo tudo

```bash
cp .env.example .env
docker compose up -d
```

Isso sobe `postgres` (17), `ollama`, um job `ollama-init` que baixa o modelo (`OLLAMA_MODEL`, padrão
`llama3.2:3b`) e a `mycroft-api` em `http://localhost:8080`. A documentação interativa (Scalar) fica em
`http://localhost:8080/scalar/v1` e o OpenAPI em `/openapi/v1.json`.

> O primeiro `up` demora o tempo de baixar o modelo (~2 GB para o 3B). A API só sobe depois do pull.

## Uso rápido

```bash
curl -X POST http://localhost:8080/api/v1/reconciliations/batch \
  -H "Content-Type: application/json" \
  -d '{
    "externalReferenceId": "fatura-out-2026",
    "callbackUrl": "https://seu-sistema/webhooks/mycroft",
    "webhookSecret": "s3cr3t",
    "customCategories": [{
      "name": "Alimentação",
      "subcategories": ["Restaurante", "Delivery"],
      "aliases": [
        { "pattern": "IFOOD%",     "merchantName": "iFood",                "subcategory": "Delivery" },
        { "pattern": "%PIPIPOPO%", "merchantName": "Restaurante Pipipopo", "subcategory": "Restaurante" }
      ]
    }],
    "transactions": [
      { "externalLineId": "tx-01", "rawDescription": "COMPRA CARTAO EUPIPIPOPO 45,90 02/10", "rawAmount": -45.90, "rawDate": "2026-10-02" }
    ]
  }'
# => 202 { "jobId": "...", "status": "Pending", "statusUrl": "/api/v1/reconciliations/...", "duplicate": false }
```

Upload de arquivo (formato detectado automaticamente; envie `format` para forçar):

```bash
curl -F "file=@extrato.ofx" -F "callbackUrl=https://seu-sistema/webhooks/mycroft" \
  http://localhost:8080/api/v1/reconciliations/upload
```

## Endpoints

| Método | Rota | Descrição |
|---|---|---|
| `POST` | `/api/v1/reconciliations/batch` | Lote JSON → `202` (ou `200` se já existe um job concluído idêntico) |
| `POST` | `/api/v1/reconciliations/upload` | `multipart/form-data`: `file`, `format?`, `externalReferenceId?`, `callbackUrl?`, `webhookSecret?`, `customCategories?` (JSON) |
| `GET` | `/api/v1/reconciliations/{jobId}` | Status, métricas (alias/cache/fallback) e transações. `?includeTransactions=false` para só o status |
| `PATCH` | `/api/v1/transactions/{id}/feedback` | Corrige merchant/categoria e ensina o cache |
| `GET`/`POST` | `/api/v1/categories` | Taxonomia padrão (11 categorias semeadas) |
| `GET` | `/health` | PostgreSQL + Ollama (`Degraded` se o modelo ainda não foi baixado) |

## Como uma transação é resolvida

1. **Normalização** — gera a `NormalizedSignature` removendo datas, valores, parcelas e códigos variáveis
   (`PGTO*IFOOD*SAOPAULO*BR 45,90` → `PGTO IFOOD SAOPAULO BR`).
2. **Alias do Job** — curingas estilo `LIKE`: `ABC` (token exato), `ABC%` (começa com), `%ABC` (termina com),
   `%ABC%` (contém). Avaliados na assinatura inteira e em fronteiras de token. **Vence o maior padrão** sem
   curingas (`%PIPIPOPO%` ganha de `%POPO%`). Com valor já estruturado → resolve com `confidenceScore = 1.0`
   sem chamar o SLM. Em linhas de PDF (texto livre) o alias vira *hint* determinístico no prompt.
3. **Cache global (`MerchantPatterns`)** — padrões com confiança ≥ 0.90 e categoria presente na taxonomia do job.
   Padrões só com merchant (aprendidos de categorias privadas) viram *hint*.
4. **SLM** — chunks de `SLM_CHUNK_SIZE` itens, `ChatResponseFormat.ForJsonSchema` com a categoria restrita por
   `enum` à taxonomia do job. Linhas com a mesma assinatura no mesmo lote são enviadas uma única vez.
   Valor e data vindos da fonte **sempre** prevalecem sobre o modelo.
5. **Fallbacks** — (1) retry do chunk com o erro de validação, (2) isolamento item a item,
   (3) `RegexFallbackExtractor` com `confidenceScore = 0.2` e `needsReview = true`. Um item ruim nunca derruba o lote;
   o job termina como `CompletedWithWarnings`.

O cache aprende com o **feedback humano**: `PATCH /feedback` promove o padrão completo (confiança 1.0) se a categoria
for da taxonomia padrão; para categorias customizadas, salva apenas merchant, meio de pagamento e recorrência —
categorias privadas nunca vazam para o cache global. Gravar respostas do SLM diretamente no cache é opcional
(`SLM_AUTO_LEARN=true`) e vem desligado: modelos pequenos dão confiança alta a respostas erradas, e um erro em cache
se repete em todos os jobs seguintes sem passar pelo modelo.

Respostas degeneradas (o mesmo merchant copiado para 3+ descrições diferentes numa chamada) são rejeitadas e
reperguntadas item a item.

**Qualidade × velocidade:** `llama3.2:3b` roda em CPU modesta mas erra descrições crípticas (`PAY EMV C`). Para
melhor categorização use um modelo 7–8B (`OLLAMA_MODEL=qwen2.5:7b` ou `llama3.1:8b`, ~2–3× mais lento em CPU) e
cadastre aliases para os estabelecimentos recorrentes — eles resolvem sem SLM e com 100% de acerto.

## Garantias operacionais

- **Durabilidade**: o job (e as linhas do lote JSON) é gravado no PostgreSQL antes do `202`. No boot, jobs
  `Pending`/`Processing` são reenfileirados; o processamento faz checkpoint por fatia e retoma só o que faltou.
- **Idempotência**: mesmo `externalReferenceId` devolve o job existente. Sem `externalReferenceId`, o mesmo hash
  SHA-256 do payload nas últimas 24h faz o mesmo. Para reprocessar um arquivo, envie um `externalReferenceId` novo.
  Exceção: um job `Failed` reenviado com o mesmo `externalReferenceId` é reprocessado com o novo payload, mantendo o `jobId`.
- **Webhook**: `POST` com `X-Mycroft-Signature: sha256=<hmac_hex>` (HMAC-SHA256 do corpo exato) e
  `X-Mycroft-Event` (`reconciliation.completed` | `reconciliation.failed`). 1 tentativa imediata + 5 retries
  (15s, 1m, 5m, 15m, 1h).
- **Retenção**: `RawFilePayload` é zerado ao concluir; jobs com mais de `RETENTION_DAYS` são removidos de hora
  em hora. `MerchantPatterns` e `Categories` são permanentes.

Validando a assinatura no cliente (C#):

```csharp
var expected = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), rawBody));
var ok = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(header));
```

## Segurança

- **Exposição**: o compose publica a porta só em `127.0.0.1`. Para expor na rede, defina `MYCROFT_BIND_ADDRESS=0.0.0.0`
  **e** `MYCROFT_API_KEY`.
- **Autenticação**: com `MYCROFT_API_KEY` definido, toda chamada a `/api/*` exige o header `X-Api-Key`.
  `/health`, `/openapi` e `/scalar` continuam públicos (o Scalar mostra um campo para a chave). Sem a chave, qualquer
  um que alcance a porta consegue enviar jobs e, via feedback, reescrever o cache global de merchants.
- **Arquivos hostis**: uploads limitados por `MAX_UPLOAD_SIZE_MB`; linhas de PDF com mais de 500 caracteres são
  descartadas e o regex de linha roda em tempo linear (`NonBacktracking`).
- **Webhooks**: o `webhookSecret` nunca é devolvido pela API. O servidor faz `POST` para qualquer `callbackUrl`
  informada, inclusive endereços internos — restrinja o acesso à API (chave + rede) se isso for um risco no seu ambiente.

## Formatos suportados

| Formato | Observações |
|---|---|
| CSV | Delimitador `;` `,` tab `|` detectado. Cabeçalhos pt/en: `data/date`, `descricao/historico/description`, `valor/amount`, opcional `id/fitid`, `tipo` (D/C). UTF-8 ou Latin-1 |
| OFX | 1.x (SGML) e 2.x (XML); `NAME` + `MEMO`, `TRNAMT`, `DTPOSTED`, `FITID` |
| CNAB 240 | Extrato para conciliação (registro `3`, segmento `E`) |
| CNAB 400 | Retorno de cobrança: apenas ocorrências de liquidação (`06,07,08,15,16,17`), layout Bradesco/Itaú |
| PDF | Texto nativo via PdfPig; cada linha com valor monetário vira uma transação de texto livre. PDF escaneado → `UnreadablePdfText` |

## Configuração

Todas as variáveis estão em [.env.example](.env.example). Principais: `OLLAMA_MODEL`, `MAX_CONCURRENT_SLM_REQUESTS`
(semáforo contra OOM), `SLM_CHUNK_SIZE`, `MAX_UPLOAD_SIZE_MB`, `MAX_ITEMS_PER_JOB`, `RETENTION_DAYS`,
`WEBHOOK_DEFAULT_SECRET`. Fora do Docker, a connection string vem de `ConnectionStrings__Mycroft`.

## Desenvolvimento

```bash
dotnet test tests/Mycroft.UnitTests
dotnet test tests/Mycroft.IntegrationTests   # requer Docker (Testcontainers + PostgreSQL 17); sem Docker os testes são ignorados
```

Nova migration:

```bash
dotnet tool restore
dotnet ef migrations add <Nome> --project src/Mycroft.Infrastructure --startup-project src/Mycroft.Infrastructure --output-dir Persistence/Migrations
```

Estrutura: `Mycroft.Core` (domínio, sem dependências), `Mycroft.Infrastructure` (EF Core, parsers, SLM, webhooks,
pipeline), `Mycroft.Api` (Minimal APIs, `Channel<T>`, workers). Licença [MIT](LICENSE).
