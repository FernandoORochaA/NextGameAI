(() => {
    "use strict";
    const root = document.querySelector(".catalog-search");
    if (!root) return;
    const input = document.getElementById("catalog-term");
    const form = document.getElementById("catalog-form");
    const status = document.getElementById("catalog-status");
    const results = document.getElementById("catalog-results");
    let timer, pending, version = 0, lastTerm = "";
    const normalize = () => input.value.normalize("NFC").trim().replace(/\s+/g, " ");
    function message(text, loading = false) {
        status.textContent = text;
        results.setAttribute("aria-busy", String(loading));
    }
    function render(games) {
        const fragment = document.createDocumentFragment();
        for (const game of games) {
            const item = document.createElement("li");
            item.className = "catalog-card";
            const fallback = document.createElement("div");
            fallback.className = "catalog-cover catalog-cover-missing";
            fallback.textContent = "Capa indisponível";
            // Defense in depth: only allow the server's fixed IGDB image host and path.
            if (typeof game.coverUrl === "string" &&
                /^https:\/\/images\.igdb\.com\/igdb\/image\/upload\/t_cover_big\/[A-Za-z0-9_-]+\.jpg$/.test(game.coverUrl)) {
                const img = document.createElement("img");
                img.className = "catalog-cover";
                img.src = game.coverUrl;
                img.alt = "Capa de " + game.title;
                img.loading = "lazy";
                img.width = 132;
                img.height = 176;
                img.addEventListener("error", () => img.replaceWith(fallback), { once: true });
                item.append(img);
            } else item.append(fallback);
            const info = document.createElement("div");
            const heading = document.createElement("h2");
            heading.textContent = game.title;
            const metadata = document.createElement("p");
            metadata.textContent = [game.releaseYear ?? "Ano não informado", game.gameType, game.editionTitle].filter(Boolean).join(" · ");
            const id = document.createElement("small");
            id.className = "text-muted";
            id.textContent = "IGDB " + game.igdbId;
            info.append(heading, metadata, id);
            const add = document.createElement("a");
            add.className = "btn btn-outline-dark d-block mt-2";
            add.textContent = "Adicionar à biblioteca";
            add.href = "/Library/Create?igdbId=" + encodeURIComponent(game.igdbId);
            info.append(add);
            item.append(info);
            fragment.append(item);
        }
        results.replaceChildren(fragment);
    }
    async function search(current, term) {
        if (current !== version || term === lastTerm) return;
        lastTerm = term;
        pending = new AbortController();
        message("Pesquisando jogos…", true);
        try {
            const url = new URL(root.dataset.searchUrl, window.location.origin);
            url.searchParams.set("term", term);
            const response = await fetch(url, { signal: pending.signal, headers: { Accept: "application/json" }, cache: "no-store" });
            if (current !== version) return;
            if (!response.ok) {
                const messages = {
                    400: "Digite entre 3 e 100 caracteres para pesquisar.",
                    429: "Muitas pesquisas em pouco tempo. Aguarde um momento e tente novamente."
                };
                const error = await response.json().catch(() => ({}));
                if (current !== version) return;
                const text = error.code === "authentication"
                    ? "Não foi possível autorizar a pesquisa. Tente novamente mais tarde."
                    : messages[response.status] ?? (response.status === 503
                        ? "A pesquisa de jogos está temporariamente indisponível. Tente novamente mais tarde."
                        : "Não foi possível concluir a pesquisa. Tente novamente.");
                throw new Error(text);
            }
            const games = await response.json();
            if (current !== version) return;
            if (!Array.isArray(games)) throw new Error("Não foi possível concluir a pesquisa. Tente novamente.");
            render(games);
            message(games.length ? games.length + (games.length === 1 ? " jogo encontrado." : " jogos encontrados.") : "Nenhum jogo encontrado.");
        } catch (error) {
            if (current !== version || error.name === "AbortError") return;
            lastTerm = ""; // Allow retry after a failure.
            message(error instanceof TypeError ? "Não foi possível conectar à pesquisa. Verifique sua conexão e tente novamente." : error.message);
        }
    }
    function schedule(immediate = false) {
        const term = normalize();
        if (term === lastTerm && term.length >= 3) return;
        clearTimeout(timer);
        pending?.abort();
        const current = ++version;
        results.replaceChildren();
        if (term.length < 3 || term.length > 100) {
            lastTerm = "";
            message(term ? "Digite entre 3 e 100 caracteres para pesquisar." : "Digite o nome de um jogo para começar.");
            return;
        }
        // Editing the input invalidates the preceding request, even during debounce.
        lastTerm = "";
        message("Aguardando para pesquisar…");
        if (immediate) search(current, term);
        else timer = setTimeout(() => search(current, term), 400);
    }
    input.addEventListener("input", () => schedule());
    form.addEventListener("submit", event => { event.preventDefault(); schedule(true); });
})();
