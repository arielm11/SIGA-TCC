// Helper único de download de arquivo binário (blob) para o Blazor WASM, usado pelas
// telas do Coordenador que baixam o PDF da ata (RF-05). Recebe os bytes já em base64
// (o C# converte via Convert.ToBase64String) e materializa o download via um link
// temporário com uma Object URL — evita manter o arquivo inteiro como data URI na tag <a>.
window.downloadFileFromBytes = (fileName, contentType, bytesBase64) => {
    const bytes = atob(bytesBase64);
    const buffer = new Uint8Array(bytes.length);

    for (let i = 0; i < bytes.length; i++) {
        buffer[i] = bytes.charCodeAt(i);
    }

    const blob = new Blob([buffer], { type: contentType });
    const url = URL.createObjectURL(blob);

    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);

    URL.revokeObjectURL(url);
};
