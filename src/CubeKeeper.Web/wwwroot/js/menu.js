// Cierra el menú plegable del móvil al elegir una página (la navegación de Blazor no recarga la cabecera).
document.addEventListener('click', e => {
  if (e.target.closest('#main-nav a')) {
    const toggle = document.getElementById('nav-toggle');
    if (toggle) toggle.checked = false;
  }
});
