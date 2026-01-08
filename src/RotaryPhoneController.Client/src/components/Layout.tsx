import React from 'react';
import { AppBar, Toolbar, Typography, Container, Box, Button } from '@mui/material';
import { Link as RouterLink } from 'react-router-dom';

interface LayoutProps {
  children: React.ReactNode;
}

export const Layout: React.FC<LayoutProps> = ({ children }) => {
  return (
    <Box sx={{ display: 'flex', flexDirection: 'column', minHeight: '100vh' }}>
      <AppBar position="static" color="default" elevation={1}>
        <Toolbar>
          <Typography variant="h6" component="div" sx={{ flexGrow: 1, color: 'primary.main' }}>
            ROTARY<span style={{ color: '#ff0055' }}>/</span>CONTROLLER
          </Typography>
          <Button component={RouterLink} to="/" color="inherit">DASHBOARD</Button>
          <Button component={RouterLink} to="/contacts" color="inherit">CONTACTS</Button>
          <Button component={RouterLink} to="/history" color="inherit">HISTORY</Button>
        </Toolbar>
      </AppBar>
      <Container component="main" maxWidth="lg" sx={{ mt: 4, mb: 4, flexGrow: 1 }}>
        {children}
      </Container>
      <Box component="footer" sx={{ py: 3, px: 2, mt: 'auto', backgroundColor: 'background.paper', borderTop: '1px solid #333' }}>
        <Container maxWidth="sm">
          <Typography variant="body2" color="text.secondary" align="center">
            Rotary Phone Controller v3.0 // Windows NUC Edition
          </Typography>
        </Container>
      </Box>
    </Box>
  );
};
