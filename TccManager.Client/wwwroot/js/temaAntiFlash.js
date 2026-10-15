// Issue #148 (achado B9, extraído do index.html em 2026-10-11 para eliminar 'unsafe-inline'
// de script-src na CSP): aplica o tema Radzen (Standard / Standard Dark) ANTES do WASM
// iniciar, para não haver flash do tema errado no primeiro paint. Lê a mesma chave de
// localStorage ("preferenciaTema") usada por TccManager.Client/Services/TemaService.cs.
// Usamos a variante "-base" dos temas porque o app convive com Bootstrap nas páginas ainda
// não migradas para Radzen (a variante "-base" não reseta elementos HTML globais).
(function () {
    var tema = 'standard-base';
    try {
        var valorSalvo = window.localStorage.getItem('preferenciaTema');
        if (valorSalvo && valorSalvo.replace(/"/g, '') === 'standard-dark') {
            tema = 'standard-dark-base';
        }
    } catch (e) {
        // localStorage indisponível (ex.: navegação privada) — mantém o tema claro padrão.
    }

    var link = document.createElement('link');
    link.id = 'radzen-theme';
    link.rel = 'stylesheet';
    link.href = '_content/Radzen.Blazor/css/' + tema + '.css';
    document.head.appendChild(link);
})();
