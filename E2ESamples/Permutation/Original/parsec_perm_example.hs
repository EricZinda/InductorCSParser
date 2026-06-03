-- A permutation parser built with Haskell's parsec, via Text.Parsec.Perm.
--
-- Text.Parsec.Perm, Copyright (c) Daan Leijen 1999-2001, (c) Paolo Martini
-- 2007, distributed under a BSD-style license (see the parsec LICENSE file:
-- https://hackage.haskell.org/package/parsec). The algorithm is from the
-- paper "Parsing Permutation Phrases" by Arthur Baars, Andres Loh, and
-- Doaitse Swierstra.
--
-- This program is a representative use of that module, written from its
-- documented API, not copied from a single upstream file. It's kept for
-- reference only; Haskell doesn't build in this .NET solution. The
-- InductorParser rewrite is in ../Rewrite/, and ../README.md compares them.
--
-- `permute` runs each parser exactly once, in whatever order the input
-- presents them. The combining function `f` (here the Box constructor) gets
-- its arguments in the order the parsers are written, regardless of input
-- order. Each parser passed with (<||>) must consume input (no empty match).

module BoxParser where

import Text.Parsec
import Text.Parsec.String (Parser)
import Text.Parsec.Perm   (permute, (<$$>), (<||>))

data Box = Box { width :: Int, height :: Int, depth :: Int }
  deriving (Eq, Show)

-- An attribute is "<name>=<digits>", optionally preceded by spaces, so the
-- three attributes can be written separated by whitespace in any order.
attribute :: String -> Parser Int
attribute name = do
  spaces
  _ <- string name
  _ <- char '='
  read <$> many1 digit

-- The permutation: width, height, and depth, each exactly once, any order.
box :: Parser Box
box = permute (Box
  <$$> attribute "width"
  <||> attribute "height"
  <||> attribute "depth")

-- These all parse to Box 4 5 6:
--   "width=4 height=5 depth=6"
--   "depth=6 width=4 height=5"
--   "height=5 depth=6 width=4"
-- This fails (depth never appears):
--   "width=4 height=5"
-- This fails (width appears twice, height never):
--   "width=4 width=5 depth=6"
