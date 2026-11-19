"use strict";
window.vecto = {
  createUrl(bytes, mime) {
    return URL.createObjectURL(new Blob([bytes], { type: mime }));
  },
  revoke(url) {
    if (typeof url === "string" && url.startsWith("blob:")) URL.revokeObjectURL(url);
  },
  downloadUrl(url, filename) {
    const link = document.createElement("a");
    link.href = url;
    link.download = filename;
    document.body.appendChild(link);
    link.click();
    link.remove();
  }
};
