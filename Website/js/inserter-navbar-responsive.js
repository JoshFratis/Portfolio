function insertNavbar(activeId = null) {
  //Get parent container
  const parent = document.getElementById("navbar");

  //Set inner html
  parent.innerHTML = `
  <div class="topnav" id="myTopnav">
    <a style="float:left" href="index.html" id="home">
      <span id="name">Joshua Fratis</span>
      <span id="initials">JF</span>
    </a>
    <a class="dummy" href="#">_</a>
    <a id="contact" href="contact.html">Contact</a>
    <a href="docs/Fratis-Joshua_Resume_09-20-23.pdf" target="_blank">Resume</a>
    <a id="portfolio" href="portfolio.html" class="dropdownButton">Portfolio</a>
    <a id="terrasim" href="terrasim.html">TerraSim</a>
    <a id="steelpunk" href="steelpunk.html">STEELPUNK</a>
    <a href="javascript:void(0);" class="icon" onclick="foldNavbar()">
      <i class="fa fa-bars"></i>
    </a>
  </div>`;

  if (activeId != null) {
    const activeElement = document.getElementById(activeId);
    activeElement.classList.add("active");
  }
}

function foldNavbar() {
  var x = document.getElementById("myTopnav");
  if (x.className === "topnav") {
    x.className += " responsive";
  } else {
    x.className = "topnav";
  }
}
