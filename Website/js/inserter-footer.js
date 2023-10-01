function insertFooter() {
  //Get parent container
  const parent = document.getElementById("footer");

  //Set inner html
  parent.innerHTML = `
    <div id="footer-contents" style="background-color: var(--color-bg-dark); color: var(--color-text-light);">
        <div class="w-full p-4">
        <p>
            Email: <a href="mailto:frajosk@gmail.com">frajosk@gmail.com</a>
            <span id="phone-number">&emsp;Phone: 484-877-0741</span>
        </p>
        </div>
    </div>`;
}
