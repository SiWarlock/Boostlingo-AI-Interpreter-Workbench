# Railway Deploy Runbook — single-service (same-origin)

> Deploy the AI Interpreter Workbench to Railway as **one .NET service that serves both the API and the built React SPA** (same-origin). One URL, no CORS, and the cascade WebSocket works without cross-origin juggling. This is the topology chosen because cascade mode relies on a long-lived WebSocket that Vercel can't host or proxy.

**Legend:** 👤 = a step **you** must do (account / interactive login / entering secret keys — an assistant cannot create accounts or type API keys into fields). 🤖 = a step the assistant can run for you.

---

## What's already wired in the repo (done)

- **`Dockerfile`** (repo root) — multi-stage: builds the SPA (`npm ci && vite build`) → publishes the .NET API → drops the SPA into `wwwroot` → a small runtime image. Binds Kestrel to Railway's `$PORT`.
- **`.dockerignore`** — keeps `.env` and build cruft out of the image (secrets never get baked in).
- **`railway.json`** — tells Railway to build from the Dockerfile and health-check `/api/health`.
- **`Program.cs`** — serves the SPA from `wwwroot` + SPA fallback, **self-gated on the build being present** (so local dev via Vite and the test suite are unaffected — verified: 494 backend tests green, 0 warnings).
- The frontend already derives every REST call + the `wss://` WebSocket relatively from `window.location`, so **no frontend env or code changes are needed** — same-origin "just works."

The image builds and runs locally (verified with `docker build` + `docker run`). Railway runs the same Dockerfile.

---

## Prerequisites

| | |
|---|---|
| 👤 A **Railway account** | Sign up at <https://railway.app> (an assistant can't create accounts). |
| ✅ The **Railway CLI** | Already installed (`railway --version` → 4.x). If not: `npm i -g @railway/cli` or `brew install railway`. |
| 👤 Your **provider keys** | An OpenAI API key and a Deepgram API key. These go into Railway as env vars — **you enter them**, never committed. |
| ✅ **Docker** | Used only for the optional local build check; Railway builds server-side. |

---

## Step-by-step

### 1. 👤 Log in to Railway (interactive)

In this session, run it with the `!` prefix so the browser-based login happens in your terminal:

```
! railway login
```

### 2. 🤖/👤 Create (or link) a Railway project

From the repo root:

```
railway init        # creates a new project; follow the prompts to name it
```

(Or `railway link` if you've already created the project in the dashboard.)

### 3. 👤 Set the environment variables

The **two secret keys are yours to enter** (an assistant won't type API keys into a field). Either paste them in the Railway **dashboard → your service → Variables**, or run these yourself with your real values:

```
railway variables --set "OPENAI_API_KEY=sk-...your key..."
railway variables --set "DEEPGRAM_API_KEY=...your key..."
```

Optional overrides (all have sane defaults baked into the image — set only if you want to change them):

```
railway variables --set "OPENAI_TRANSLATION_MODEL=gpt-5-nano"   # or gpt-5-mini
railway variables --set "OPENAI_REALTIME_MODEL=gpt-realtime"    # or gpt-realtime-mini
```

> `ASPNETCORE_ENVIRONMENT=Production`, `PRICING_CONFIG_PATH`, and `SESSION_DATA_DIR` are already set inside the image — you don't need to add them. `PORT` is injected by Railway automatically. **`FRONTEND_ORIGIN` is set in Step 6** (it needs the domain, which doesn't exist until the first deploy).

### 4. 🤖 Deploy

```
railway up
```

This uploads the build context and builds the Dockerfile on Railway. First build takes a few minutes (npm + dotnet). Watch the logs until it's live.

### 5. 🤖/👤 Get a public domain

In the Railway dashboard (**Settings → Networking → Generate Domain**) or:

```
railway domain
```

You'll get something like `https://ai-interpreter-workbench-production.up.railway.app`. **Copy it.**

### 6. 👤/🤖 Set `FRONTEND_ORIGIN` to that domain, then redeploy

This is **required for cascade mode** — the cascade WebSocket validates the browser's `Origin` against `FRONTEND_ORIGIN` (a WS upgrade bypasses CORS, so the endpoint checks it itself). Until it's set, cascade turns are rejected (realtime still works, since it talks directly to OpenAI).

```
railway variables --set "FRONTEND_ORIGIN=https://<your-exact-railway-domain>"
railway up        # or trigger a redeploy so the new value is picked up
```

> Use the **exact** scheme + host (`https://…`, no trailing slash) — the check is an exact-match.

### 7. 🤖/👤 Verify

1. **Health:** open `https://<domain>/api/health` → `{"status":"ok"}`.
2. **App:** open `https://<domain>/` → the workbench loads; mic permission prompts (HTTPS satisfies the secure-context requirement for `getUserMedia`/WebRTC).
3. **Realtime turn:** Realtime mode → speak → hear the translation.
4. **Cascade turn:** Cascade mode → speak → see per-stage metrics + hear audio. (If cascade fails with a connection/forbidden error, re-check Step 6 — `FRONTEND_ORIGIN` must equal the live domain exactly.)

---

## Notes & gotchas

- **Ephemeral filesystem.** Railway containers don't persist disk across redeploys/restarts, so session JSON under `/app/data/sessions` is **transient** — fine for an evaluation/demo workbench (sessions are throwaway evidence). If you want sessions to survive redeploys, add a **Railway Volume** mounted at `/app/data` and the data dir persists.
- **HTTPS is automatic** and required — mic capture (`getUserMedia`) and WebRTC only work over HTTPS or `localhost`. Railway gives you HTTPS out of the box.
- **Dev-only surfaces are absent in Production** by design — the `POST /api/dev/tts` endpoint and the `?soak=1` harness are Development-gated and won't exist in the deployed build. That's intentional; they're local tooling.
- **Secrets live only in Railway env vars.** Never commit a key; `.env` is gitignored and `.dockerignore`'d so it can't enter the image.
- **Costs are real.** Every live turn calls OpenAI + Deepgram on your keys. Realtime is ~$0.24/min and climbs with conversation length (see `docs/COMPARISON_WRITEUP.md`).

## Optional: deploy-on-push instead of `railway up`

If you'd rather Railway auto-build on every `git push`: create a GitHub repo, push this code, and in Railway connect the service to that repo (Settings → Source). Then each push to the chosen branch redeploys. (There's no git remote configured today; `railway up` from the CLI is the zero-remote path and is fine for a demo.)
