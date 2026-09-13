// 配布ページの動作。
//
// このファイルは VRChat 公式のリスト生成ツール（vrchat-community/package-list-action）が
// 必ず読み込む。無いとページの生成そのものが失敗し、「VCCに追加」が使えなくなる。
// ツールがこのファイルを Scriban テンプレートとして処理するので、
// {{ listingInfo.Url }} は公開時にリストの URL へ置き換わる。

const LISTING_URL = "{{ listingInfo.Url }}";

(function () {
  // VCC を呼び出すリンク。形式は公式テンプレートと同じ vcc://vpm/addRepo。
  const addButton = document.getElementById("vccAddRepoButton");
  if (addButton && LISTING_URL) {
    addButton.href = "vcc://vpm/addRepo?url=" + encodeURIComponent(LISTING_URL);
  }

  // URL のコピー
  const copyButton = document.getElementById("vccUrlFieldCopy");
  if (copyButton) {
    copyButton.addEventListener("click", function () {
      const done = function () {
        copyButton.textContent = "コピーしました";
        setTimeout(function () { copyButton.textContent = "コピー"; }, 2000);
      };
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(LISTING_URL).then(done, function () {});
      }
    });
  }
})();
