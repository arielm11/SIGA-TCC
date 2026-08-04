// Interop mínimo usado pelo TemaService (TccManager.Client/Services/TemaService.cs) para trocar
// o tema Radzen em runtime, sem recarregar a página. O tema é puramente CSS: basta trocar o
// href do <link id="radzen-theme"> (criado pelo script inline em index.html) para reestilizar
// toda a árvore de componentes Radzen instantaneamente.
//
// Usamos a variante "-base" dos temas Standard/Standard Dark (standard-base.css /
// standard-dark-base.css) porque o app convive com Bootstrap: a variante "-base" não reseta
// elementos HTML globais (h1-h6, body etc.), evitando conflito com os estilos Bootstrap já
// aplicados nas páginas ainda não migradas para Radzen.
// A troca do href dispara um carregamento assíncrono do novo stylesheet; para a View
// Transitions API (efeito de crossfade suave, ver wwwroot/css/radzen-overrides.css) capturar o
// estado "depois" corretamente, esperamos o evento "load" do novo <link> antes de considerar a
// transição concluída — sem isso, o crossfade poderia ser fotografado antes do CSS novo aplicar.
window.setRadzenTheme = function (nomeTema) {
    var link = document.getElementById('radzen-theme');
    if (!link) {
        return;
    }

    var arquivo = nomeTema === 'standard-dark' ? 'standard-dark-base' : 'standard-base';
    var novoHref = '_content/Radzen.Blazor/css/' + arquivo + '.css';

    var aplicar = function () {
        return new Promise(function (resolve) {
            var onLoad = function () {
                link.removeEventListener('load', onLoad);
                resolve();
            };
            link.addEventListener('load', onLoad);
            link.setAttribute('href', novoHref);
        });
    };

    if (document.startViewTransition) {
        document.startViewTransition(aplicar);
    } else {
        aplicar();
    }
};
